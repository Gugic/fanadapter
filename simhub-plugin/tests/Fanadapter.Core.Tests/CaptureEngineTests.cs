using System;
using System.Collections.Generic;
using Fanadapter.Core;
using Xunit;

namespace Fanadapter.Core.Tests
{
    /// <summary>
    /// The capture flow is the hardest thing here to verify by hand — every
    /// case needs a rig, a physical control and precise timing. These cover the
    /// awkward ones: a pedal that rests high, a device silent through the
    /// baseline window, a press that never gets released, jitter that must not
    /// commit anything.
    /// </summary>
    public class CaptureEngineTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly DeviceSlot Device = new DeviceSlot
        {
            Slot = 0, Connected = true, Vid = 0x046D, Pid = 0xC26B,
            AxisCount = 2, ButtonCount = 8,
        };

        private static CaptureEngine NewEngine(string channel = "throttle", int slot = 0) =>
            new CaptureEngine(channel, slot, T0, s => s == Device.Slot ? Device : null);

        private static LiveSlot Frame(uint buttons = 0, int[] axes = null, int? hat = null,
            bool hasHat = false, int[] keys = null)
        {
            return new LiveSlot
            {
                Slot = 0,
                Buttons = buttons,
                Axes = axes ?? new int[0],
                Hat = hat,
                HasHat = hasHat || hat.HasValue,
                Keys = keys,
            };
        }

        /// <summary>Feeds identical resting frames through the baseline window.</summary>
        private static DateTime SettleBaseline(CaptureEngine engine, LiveSlot resting, int frames = 8)
        {
            var now = T0;
            for (int i = 0; i < frames; i++)
            {
                now = now.AddMilliseconds(40);
                engine.Observe(resting, now);
            }
            now = T0 + CaptureEngine.BaselineWindow.Add(TimeSpan.FromMilliseconds(10));
            engine.Tick(now);
            return now;
        }

        [Fact]
        public void StartsInBaselineAndLeavesItOnTime()
        {
            var engine = NewEngine();
            Assert.Equal(CapturePhase.Baseline, engine.Phase);

            engine.Tick(T0.AddMilliseconds(100));
            Assert.Equal(CapturePhase.Baseline, engine.Phase);

            engine.Tick(T0 + CaptureEngine.BaselineWindow);
            Assert.Equal(CapturePhase.Active, engine.Phase);
        }

        [Fact]
        public void ButtonCommitsImmediately()
        {
            var engine = NewEngine("gear_1");
            var now = SettleBaseline(engine, Frame());

            engine.Observe(Frame(buttons: 1u << 5), now.AddMilliseconds(50));

            Assert.Equal(CapturePhase.Committed, engine.Phase);
            Assert.Equal(InputType.Button, engine.Result.Binding.Type);
            Assert.Equal(5, engine.Result.Binding.Index);
            Assert.Equal(0x046D, engine.Result.Binding.Vid);
            Assert.Equal("gear_1", engine.Result.Channel);
        }

        [Fact]
        public void ButtonHeldThroughBaselineIsNotCaptured()
        {
            // Otherwise a shifter resting in gear would bind itself the instant
            // capture started, before the user touched anything.
            var engine = NewEngine("gear_1");
            var now = SettleBaseline(engine, Frame(buttons: 1u << 3));

            engine.Observe(Frame(buttons: 1u << 3), now.AddMilliseconds(50));
            Assert.Equal(CapturePhase.Active, engine.Phase);

            engine.Observe(Frame(buttons: (1u << 3) | (1u << 6)), now.AddMilliseconds(100));
            Assert.Equal(CapturePhase.Committed, engine.Phase);
            Assert.Equal(6, engine.Result.Binding.Index);
        }

        [Fact]
        public void LowestNewBitWinsWhenTwoArrivetogether()
        {
            var engine = NewEngine();
            var now = SettleBaseline(engine, Frame());

            engine.Observe(Frame(buttons: (1u << 2) | (1u << 7)), now.AddMilliseconds(50));

            Assert.Equal(2, engine.Result.Binding.Index);
        }

        [Fact]
        public void HatCommitsOnAMoveIntoADirection()
        {
            var engine = NewEngine("shift_up");
            var now = SettleBaseline(engine, Frame(hat: -1, hasHat: true));

            engine.Observe(Frame(hat: 2, hasHat: true), now.AddMilliseconds(50));

            Assert.Equal(CapturePhase.Committed, engine.Phase);
            Assert.Equal(InputType.Hat, engine.Result.Binding.Type);
            Assert.Equal(2, engine.Result.Binding.Index);
        }

        [Fact]
        public void KeyboardScancodeCommits()
        {
            var engine = NewEngine("gear_R");
            var now = SettleBaseline(engine, Frame(keys: new[] { 0, 0, 0, 0, 0, 0 }));

            engine.Observe(Frame(keys: new[] { 0x04, 0, 0, 0, 0, 0 }), now.AddMilliseconds(50));

            Assert.Equal(InputType.Key, engine.Result.Binding.Type);
            Assert.Equal(0x04, engine.Result.Binding.Index);
        }

        [Fact]
        public void KeyHeldThroughBaselineIsWhitelisted()
        {
            var engine = NewEngine();
            var now = SettleBaseline(engine, Frame(keys: new[] { 0xE1, 0, 0, 0, 0, 0 }));

            engine.Observe(Frame(keys: new[] { 0xE1, 0, 0, 0, 0, 0 }), now.AddMilliseconds(50));
            Assert.Equal(CapturePhase.Active, engine.Phase);

            engine.Observe(Frame(keys: new[] { 0xE1, 0x1E, 0, 0, 0, 0 }), now.AddMilliseconds(100));
            Assert.Equal(0x1E, engine.Result.Binding.Index);
        }

        [Fact]
        public void RisingAxisCalibratesWithoutInvert()
        {
            var engine = NewEngine("throttle");
            var now = SettleBaseline(engine, Frame(axes: new[] { 20, 0 }));

            // Press well past the trigger, then release back to rest.
            engine.Observe(Frame(axes: new[] { 2000, 0 }), now.AddMilliseconds(50));
            Assert.Equal(CapturePhase.Tracking, engine.Phase);

            engine.Observe(Frame(axes: new[] { 4000, 0 }), now.AddMilliseconds(100));
            engine.Observe(Frame(axes: new[] { 20, 0 }), now.AddMilliseconds(200));

            Assert.Equal(CapturePhase.Committed, engine.Phase);
            var b = engine.Result.Binding;
            Assert.Equal(InputType.Axis, b.Type);
            Assert.Equal(0, b.Index);
            Assert.False(b.Invert);
            Assert.Equal(4000, b.RawMax);
            Assert.True(b.RawMin <= 20);
        }

        [Fact]
        public void FallingAxisCalibratesWithInvert()
        {
            // A pedal that rests high and drops when pressed. Without the invert
            // the wheelbase would read full throttle at rest.
            var engine = NewEngine("brake");
            var now = SettleBaseline(engine, Frame(axes: new[] { 4000, 0 }));

            engine.Observe(Frame(axes: new[] { 2000, 0 }), now.AddMilliseconds(50));
            Assert.Equal(CapturePhase.Tracking, engine.Phase);

            engine.Observe(Frame(axes: new[] { 100, 0 }), now.AddMilliseconds(100));
            engine.Observe(Frame(axes: new[] { 4000, 0 }), now.AddMilliseconds(200));

            Assert.Equal(CapturePhase.Committed, engine.Phase);
            var b = engine.Result.Binding;
            Assert.True(b.Invert);
            Assert.Equal(100, b.RawMin);
            Assert.True(b.RawMax >= 4000);

            // The calibration has to actually work: pressed reads full, rest reads none.
            Assert.Equal(65535, ScaleAxis.Apply(100, b));
            Assert.Equal(0, ScaleAxis.Apply(4000, b));
        }

        [Fact]
        public void CommittedAxisCalibrationSurvivesARoundTripThroughScaleAxis()
        {
            var engine = NewEngine("throttle");
            var now = SettleBaseline(engine, Frame(axes: new[] { 30, 0 }));

            engine.Observe(Frame(axes: new[] { 3000, 0 }), now.AddMilliseconds(50));
            engine.Observe(Frame(axes: new[] { 4095, 0 }), now.AddMilliseconds(100));
            engine.Observe(Frame(axes: new[] { 30, 0 }), now.AddMilliseconds(200));

            var b = engine.Result.Binding;
            Assert.Equal(0, ScaleAxis.Apply(30, b));
            Assert.Equal(65535, ScaleAxis.Apply(4095, b));
            Assert.InRange(ScaleAxis.Apply(2060, b), 25000, 40000);
        }

        [Fact]
        public void AxisJitterInsideTheNoiseBandCommitsNothing()
        {
            var engine = NewEngine();
            var now = SettleBaseline(engine, Frame(axes: new[] { 2048, 0 }));

            for (int i = 0; i < 20; i++)
            {
                int wobble = 2048 + (i % 2 == 0 ? 40 : -40);
                engine.Observe(Frame(axes: new[] { wobble, 0 }), now.AddMilliseconds(20 * i));
            }

            Assert.Equal(CapturePhase.Active, engine.Phase);
            Assert.Null(engine.Result);
        }

        [Fact]
        public void PressWithoutReleaseStillCalibratesAtTheDeadline()
        {
            // Someone who holds the pedal down past the deadline has already
            // shown the full travel — throwing that away would be worse than
            // committing it.
            var engine = NewEngine("throttle");
            var now = SettleBaseline(engine, Frame(axes: new[] { 10, 0 }));

            engine.Observe(Frame(axes: new[] { 3000, 0 }), now.AddMilliseconds(50));
            Assert.Equal(CapturePhase.Tracking, engine.Phase);

            engine.Tick(T0 + CaptureEngine.Deadline.Add(TimeSpan.FromMilliseconds(1)));

            Assert.Equal(CapturePhase.Committed, engine.Phase);
            Assert.True(engine.Result.FromDeadline);
            Assert.Equal(InputType.Axis, engine.Result.Binding.Type);
        }

        [Fact]
        public void NothingPressedTimesOutWithNoBinding()
        {
            var engine = NewEngine();
            SettleBaseline(engine, Frame());

            engine.Tick(T0 + CaptureEngine.Deadline.Add(TimeSpan.FromMilliseconds(1)));

            Assert.Equal(CapturePhase.TimedOut, engine.Phase);
            Assert.Null(engine.Result);
        }

        [Fact]
        public void DeviceSilentThroughBaselineStillCapturesItsFirstInput()
        {
            // Keyboards and jitter-free gamepads report only on change, so they
            // are never sampled during baseline. Without seeding a resting
            // baseline on first sight, their first press is compared against
            // nothing and silently dropped until the capture times out.
            var engine = NewEngine("gear_2");
            engine.Tick(T0 + CaptureEngine.BaselineWindow.Add(TimeSpan.FromMilliseconds(10)));
            Assert.Equal(CapturePhase.Active, engine.Phase);

            engine.Observe(Frame(buttons: 1u << 4), T0.AddSeconds(1));

            Assert.Equal(CapturePhase.Committed, engine.Phase);
            Assert.Equal(4, engine.Result.Binding.Index);
        }

        [Fact]
        public void FramesFromADisconnectedSlotAreIgnored()
        {
            var engine = new CaptureEngine("throttle", 0, T0, s => null);
            engine.Tick(T0 + CaptureEngine.BaselineWindow.Add(TimeSpan.FromMilliseconds(10)));

            engine.Observe(Frame(buttons: 1u << 1), T0.AddSeconds(1));

            Assert.Equal(CapturePhase.Active, engine.Phase);
        }

        [Fact]
        public void CancelStopsTheCapture()
        {
            var engine = NewEngine();
            var now = SettleBaseline(engine, Frame());

            engine.Cancel();
            engine.Observe(Frame(buttons: 1u << 1), now.AddMilliseconds(50));

            Assert.Equal(CapturePhase.Cancelled, engine.Phase);
            Assert.Null(engine.Result);
        }

        [Fact]
        public void MovementTooSmallToBeIntentCommitsNothing()
        {
            // 600 counts clears the 500 trigger and latches tracking, but the
            // release gate wants travel past 1.5x the trigger. A nudge that
            // never reaches that leaves the capture waiting rather than
            // committing a calibration built from half a press.
            var engine = NewEngine();
            var now = SettleBaseline(engine, Frame(axes: new[] { 100, 0 }));

            engine.Observe(Frame(axes: new[] { 700, 0 }), now.AddMilliseconds(50));
            Assert.Equal(CapturePhase.Tracking, engine.Phase);

            engine.Observe(Frame(axes: new[] { 100, 0 }), now.AddMilliseconds(150));

            Assert.Equal(CapturePhase.Tracking, engine.Phase);
            Assert.Null(engine.Result);
        }

        [Theory]
        [InlineData(100, 4000)]   // rising pedal
        [InlineData(4000, 100)]   // falling pedal
        [InlineData(2048, 3200)]  // centred axis pushed one way
        public void EveryCommittedCalibrationSpansMoreThanTheMinimumRange(int rest, int peak)
        {
            // The 256-count floor in the commit maths cannot actually bind:
            // latching needs the axis to travel past the trigger threshold, and
            // committing needs 1.5x that again, so the span is always far wider.
            // This pins the property the floor is there to guarantee, so if the
            // thresholds are ever retuned it fails here rather than silently
            // producing an unusable binding.
            var engine = NewEngine();
            var now = SettleBaseline(engine, Frame(axes: new[] { rest, 0 }));

            engine.Observe(Frame(axes: new[] { peak, 0 }), now.AddMilliseconds(50));
            engine.Observe(Frame(axes: new[] { rest, 0 }), now.AddMilliseconds(150));

            Assert.Equal(CapturePhase.Committed, engine.Phase);
            var b = engine.Result.Binding;
            Assert.True(b.RawMax - b.RawMin >= 256,
                $"span was {b.RawMax - b.RawMin}, expected at least the 256 floor");
        }
    }
}
