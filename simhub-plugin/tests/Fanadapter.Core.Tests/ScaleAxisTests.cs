using Fanadapter.Core;
using Xunit;

namespace Fanadapter.Core.Tests
{
    /// <summary>
    /// Mirror tests for the firmware's scale_axis(). These expectations are the
    /// same ones webconfig/src/lib/scaleAxis.test.ts asserts — three
    /// implementations of this function now exist (C++, TypeScript, C#), and
    /// AGENTS.md tracks them as a cross-file invariant. If the firmware math
    /// changes, all three move together.
    /// </summary>
    public class ScaleAxisTests
    {
        private static InputBinding Axis(int rawMin = 0, int rawMax = 0, int deadzoneLow = 0,
            int deadzoneHigh = 65535, bool invert = false, int threshold = 32768)
        {
            return new InputBinding
            {
                Type = InputType.Axis,
                RawMin = rawMin,
                RawMax = rawMax,
                DeadzoneLow = deadzoneLow,
                DeadzoneHigh = deadzoneHigh,
                Invert = invert,
                Threshold = threshold,
            };
        }

        [Fact]
        public void PassesRawThroughWhenUncalibrated()
        {
            var b = Axis();
            Assert.Equal(0, ScaleAxis.Apply(0, b));
            Assert.Equal(12345, ScaleAxis.Apply(12345, b));
            Assert.Equal(65535, ScaleAxis.Apply(65535, b));
        }

        [Fact]
        public void ClampsOutsideTheCalibratedRange()
        {
            var b = Axis(rawMin: 100, rawMax: 4095);
            Assert.Equal(0, ScaleAxis.Apply(50, b));
            Assert.Equal(0, ScaleAxis.Apply(100, b));
            Assert.Equal(65535, ScaleAxis.Apply(4095, b));
            Assert.Equal(65535, ScaleAxis.Apply(9999, b));
        }

        [Fact]
        public void ScalesLinearlyWithIntegerFloor()
        {
            var b = Axis(rawMin: 0, rawMax: 100);
            Assert.Equal(32767, ScaleAxis.Apply(50, b)); // floor(32767.5)
            Assert.Equal(16383, ScaleAxis.Apply(25, b)); // floor(16383.75)
        }

        [Fact]
        public void InvertsAfterScaling()
        {
            Assert.Equal(32767, ScaleAxis.Apply(50, Axis(0, 100)));
            Assert.Equal(32768, ScaleAxis.Apply(50, Axis(0, 100, invert: true)));
            Assert.Equal(65535, ScaleAxis.Apply(0, Axis(0, 100, invert: true)));
            Assert.Equal(0, ScaleAxis.Apply(100, Axis(0, 100, invert: true)));
        }

        [Fact]
        public void FloorsToZeroAtOrBelowDeadzoneLow()
        {
            var b = Axis(rawMin: 0, rawMax: 100, deadzoneLow: 10000);
            Assert.Equal(0, ScaleAxis.Apply(10, b));      // scaled 6553 → below the floor
            Assert.Equal(13107, ScaleAxis.Apply(20, b));  // above it → passes through
        }

        [Fact]
        public void SaturatesAtOrAboveDeadzoneHigh()
        {
            var b = Axis(rawMin: 0, rawMax: 100, deadzoneHigh: 50000);
            Assert.Equal(65535, ScaleAxis.Apply(90, b));  // scaled 58981 → saturates
            Assert.Equal(32767, ScaleAxis.Apply(50, b));
        }

        [Fact]
        public void AppliesDeadzonesAfterInvert()
        {
            // A pedal that rests high: raw 100 inverts to 0 and the low deadzone
            // holds it there, which is what makes the descending capture path work.
            var b = Axis(rawMin: 0, rawMax: 100, invert: true, deadzoneLow: 100);
            Assert.Equal(0, ScaleAxis.Apply(100, b));
            Assert.Equal(65535, ScaleAxis.Apply(0, b));
        }

        [Fact]
        public void EvalButtonComparesPostScaleValueToThreshold()
        {
            var b = Axis(rawMin: 0, rawMax: 100, threshold: 10000);
            Assert.False(ScaleAxis.EvalButton(10, b)); // 6553
            Assert.True(ScaleAxis.EvalButton(50, b));  // 32767
        }

        [Fact]
        public void EvalButtonHonoursDeadzoneHighSaturation()
        {
            // Saturation to 65535 beats any threshold, so a deadzoneHigh trip
            // pulls the button down with it.
            var b = Axis(rawMin: 0, rawMax: 100, deadzoneHigh: 50000, threshold: 64000);
            Assert.True(ScaleAxis.EvalButton(90, b));
        }
    }
}
