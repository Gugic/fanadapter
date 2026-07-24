using System.Collections.Generic;
using System.Linq;
using Fanadapter.Core;
using Xunit;

namespace Fanadapter.Core.Tests
{
    /// <summary>
    /// Drives the PC-side pedal auto-detect with synthetic property snapshots.
    /// Ticks are 50 ms; the detector takes explicit timestamps, so no clock.
    /// </summary>
    public class PropertyAxisDetectorTests
    {
        private const double Dt = 0.05;

        private static double Tick(PropertyAxisDetector d, double t, params (string, double)[] values)
        {
            d.Feed(t, values.Select(v => new KeyValuePair<string, double>(v.Item1, v.Item2)));
            return t + Dt;
        }

        private static double Baseline(PropertyAxisDetector d, params (string, double)[] values)
        {
            double t = 0;
            while (t <= 0.65) t = Tick(d, t, values);
            Assert.Equal(DetectPhase.Listening, d.Phase);
            return t;
        }

        [Fact]
        public void RisingAxisCommitsRangeAndProperty()
        {
            var d = new PropertyAxisDetector();
            var t = Baseline(d,
                ("InputStatus.JoystickPlugin.Pedals_Y", 0),
                ("Some.Static.Number", 42));

            // Press to 100 over a few ticks, hold, release back to 0.
            foreach (var v in new double[] { 20, 55, 90, 100, 100, 100, 60, 15, 0 })
                t = Tick(d, t, ("InputStatus.JoystickPlugin.Pedals_Y", v), ("Some.Static.Number", 42));

            Assert.Equal(DetectPhase.Done, d.Phase);
            Assert.Equal("InputStatus.JoystickPlugin.Pedals_Y", d.Result.PropertyName);
            Assert.False(d.Result.Invert);
            Assert.Equal(0, d.Result.InputMin, 1);
            Assert.Equal(100, d.Result.InputMax, 1);
        }

        [Fact]
        public void FallingAxisSetsInvert()
        {
            var d = new PropertyAxisDetector();
            var t = Baseline(d, ("JoystickPlugin.Brake", 65535));

            foreach (var v in new double[] { 50000, 20000, 300, 300, 300, 30000, 65535 })
                t = Tick(d, t, ("JoystickPlugin.Brake", v));

            Assert.Equal(DetectPhase.Done, d.Phase);
            Assert.True(d.Result.Invert);
            Assert.Equal(300, d.Result.InputMin, 0);
            Assert.Equal(65535, d.Result.InputMax, 0);
        }

        [Fact]
        public void RestlessTelemetryIsExcludedAndPedalStillWins()
        {
            var d = new PropertyAxisDetector();

            // Rpm oscillates through the baseline — a live game session.
            double t = 0;
            int i = 0;
            while (t <= 0.65)
            {
                t = Tick(d, t, ("GameData.Rpm", 3000 + (i++ % 2) * 2000), ("JoystickPlugin.Throttle", 0));
            }
            Assert.Equal(DetectPhase.Listening, d.Phase);

            // Rpm keeps flailing; the pedal is pressed. Only the pedal may latch.
            foreach (var v in new double[] { 40, 80, 100, 100, 50, 5, 0 })
                t = Tick(d, t, ("GameData.Rpm", 9000), ("JoystickPlugin.Throttle", v));

            Assert.Equal(DetectPhase.Done, d.Phase);
            Assert.Equal("JoystickPlugin.Throttle", d.Result.PropertyName);
        }

        [Fact]
        public void NothingMovingTimesOut()
        {
            var d = new PropertyAxisDetector();
            double t = 0;
            while (t <= 10.7 && d.Phase != DetectPhase.Failed)
                t = Tick(d, t, ("JoystickPlugin.Throttle", 0));

            Assert.Equal(DetectPhase.Failed, d.Phase);
            Assert.NotNull(d.FailureReason);
        }

        [Fact]
        public void InputNamespaceBeatsMirroredTelemetry()
        {
            // Both cross identically in the same tick (a mirrored/derived
            // property, or telemetry reacting to the pedal in a live session).
            var d = new PropertyAxisDetector();
            var t = Baseline(d, ("DataCorePlugin.Computed.Pedal", 0), ("InputStatus.JoystickPlugin.Pedals_Rz", 0));

            foreach (var v in new double[] { 50, 100, 100, 100, 40, 0 })
                t = Tick(d, t, ("DataCorePlugin.Computed.Pedal", v), ("InputStatus.JoystickPlugin.Pedals_Rz", v));

            Assert.Equal(DetectPhase.Done, d.Phase);
            Assert.Equal("InputStatus.JoystickPlugin.Pedals_Rz", d.Result.PropertyName);
        }

        [Fact]
        public void SingleTickSpikeDoesNotCommit()
        {
            var d = new PropertyAxisDetector();
            var t = Baseline(d, ("JoystickPlugin.Throttle", 0));

            // One stray count up, straight back down: latches, must NOT commit.
            t = Tick(d, t, ("JoystickPlugin.Throttle", 2));
            t = Tick(d, t, ("JoystickPlugin.Throttle", 0));
            Assert.Equal(DetectPhase.Listening, d.Phase);

            // A real press afterwards still works.
            foreach (var v in new double[] { 30, 70, 100, 100, 100, 100, 50, 0 })
                t = Tick(d, t, ("JoystickPlugin.Throttle", v));

            Assert.Equal(DetectPhase.Done, d.Phase);
            Assert.Equal(100, d.Result.InputMax, 1);
        }

        [Fact]
        public void ButtonAsPedalCommitsZeroOneRange()
        {
            // A button driving a pedal channel is legitimate — the caller feeds
            // bools coerced to 0/1, and AxisSource.Scale handles the rest.
            var d = new PropertyAxisDetector();
            var t = Baseline(d, ("InputStatus.JoystickPlugin.Button3", 0));

            foreach (var v in new double[] { 1, 1, 1, 1, 1, 1, 0 })
                t = Tick(d, t, ("InputStatus.JoystickPlugin.Button3", v));

            Assert.Equal(DetectPhase.Done, d.Phase);
            Assert.Equal(0, d.Result.InputMin, 2);
            Assert.Equal(1, d.Result.InputMax, 2);
            Assert.False(d.Result.Invert);
        }

        [Fact]
        public void SurvivingCandidatesShrinkAfterBaselineAndTracking()
        {
            var d = new PropertyAxisDetector();
            var t = Baseline(d, ("JoystickPlugin.Throttle", 0), ("GameData.Clock", 1000));
            // Clock moved every tick? No — it was static here; make a fresh run where it moves.
            Assert.Contains("JoystickPlugin.Throttle", d.SurvivingCandidates());

            t = Tick(d, t, ("JoystickPlugin.Throttle", 60), ("GameData.Clock", 1000));
            Assert.Equal(DetectPhase.Tracking, d.Phase);
            Assert.Equal(new[] { "JoystickPlugin.Throttle" }, d.SurvivingCandidates());
        }
    }
}
