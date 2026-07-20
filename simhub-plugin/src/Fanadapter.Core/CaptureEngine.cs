using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanadapter.Core
{
    public enum CapturePhase
    {
        /// <summary>Learning the noise floor while the user holds still.</summary>
        Baseline,

        /// <summary>Waiting for an input that clearly exceeds that noise floor.</summary>
        Active,

        /// <summary>An axis has been latched; accumulating its travel until release.</summary>
        Tracking,

        Committed,
        Cancelled,
        TimedOut,
    }

    public class CaptureResult
    {
        public string Channel { get; set; }
        public int Slot { get; set; }
        public InputBinding Binding { get; set; }

        /// <summary>True when the deadline fired rather than the user releasing.</summary>
        public bool FromDeadline { get; set; }
    }

    /// <summary>
    /// The three-phase "press the control you want" capture, ported from the
    /// same state machine webconfig runs in App.tsx. Kept free of UI and timers
    /// — the caller feeds it live frames and the current time — so the awkward
    /// parts (a pedal that rests high, a device that stays silent through the
    /// baseline window, a user who presses and never releases) can be tested
    /// instead of reproduced by hand on a rig.
    ///
    /// Why three phases: a button is unambiguous the moment a new bit appears,
    /// but an axis needs its travel measured, which means watching until the
    /// user lets go. Committing an axis early would calibrate against a partial
    /// press and every later input would read short.
    /// </summary>
    public class CaptureEngine
    {
        /// <summary>How long to watch a resting device to learn its jitter.</summary>
        public static readonly TimeSpan BaselineWindow = TimeSpan.FromMilliseconds(400);

        /// <summary>Give up if nothing is pressed by then.</summary>
        public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);

        /// <summary>Movement must beat the noise band by this much to count as intent.</summary>
        private const double TriggerNoiseMultiple = 5;
        private const double MinimumTrigger = 500;

        /// <summary>How close to the resting point counts as "released".</summary>
        private const double ReturnNoiseMultiple = 2;
        private const double MinimumReturnBand = 200;

        /// <summary>
        /// Travel needed before a return to baseline is read as a release rather
        /// than as noise that never left it.
        /// </summary>
        private const double PressedTravelMultiple = 1.5;

        /// <summary>
        /// Floor on the calibrated span. With the current thresholds this can
        /// never actually bind — latching already requires travel past
        /// MinimumTrigger and committing requires half again as much, so the
        /// span is always far wider than 256. It is kept (matching webconfig)
        /// as a guard in case those thresholds are ever lowered; see
        /// EveryCommittedCalibrationSpansMoreThanTheMinimumRange, which pins the
        /// property rather than the dead branch.
        /// </summary>
        private const int MinimumRange = 256;

        /// <summary>Deadzone above the jitter band, capped so it can't eat real motion.</summary>
        private const int DeadzoneNoiseMultiple = 3;
        private const int MaximumDeadzone = 5000;

        private sealed class Baseline
        {
            public uint Buttons;
            public readonly Dictionary<int, int> AxisMin = new Dictionary<int, int>();
            public readonly Dictionary<int, int> AxisMax = new Dictionary<int, int>();
            public int? Hat;
            public readonly HashSet<int> Keys = new HashSet<int>();
        }

        private sealed class Tracking
        {
            public int DeviceSlot;
            public int AxisIndex;
            public int Vid;
            public int Pid;
            public double BaselineMid;
            public double Noise;
            public int PeakHigh;
            public int PeakLow;
            public double TriggerThreshold;
        }

        private readonly Dictionary<int, Baseline> _baselines = new Dictionary<int, Baseline>();
        private readonly Func<int, DeviceSlot> _lookupDevice;
        private readonly DateTime _baselineEnd;
        private readonly DateTime _deadline;
        private Tracking _tracking;

        public CaptureEngine(string channel, int bindingSlot, DateTime now, Func<int, DeviceSlot> lookupDevice)
        {
            Channel = channel;
            BindingSlot = bindingSlot;
            _lookupDevice = lookupDevice;
            _baselineEnd = now + BaselineWindow;
            _deadline = now + Deadline;
        }

        public string Channel { get; }
        public int BindingSlot { get; }
        public CapturePhase Phase { get; private set; } = CapturePhase.Baseline;
        public CaptureResult Result { get; private set; }

        public bool IsFinished =>
            Phase == CapturePhase.Committed || Phase == CapturePhase.Cancelled || Phase == CapturePhase.TimedOut;

        /// <summary>
        /// Advances the clock. Phase changes are time-driven, not event-driven,
        /// so an idle device that emits nothing still leaves the baseline window
        /// and still hits the deadline.
        /// </summary>
        public void Tick(DateTime now)
        {
            if (IsFinished) return;

            if (Phase == CapturePhase.Baseline && now >= _baselineEnd)
            {
                Phase = CapturePhase.Active;
            }

            if (now >= _deadline)
            {
                // Commit whatever travel was measured rather than discarding it:
                // a user who presses and holds past the deadline still told us
                // everything we needed about that axis.
                var binding = BuildAxisBinding();
                if (binding != null) Finish(binding, fromDeadline: true);
                else Phase = CapturePhase.TimedOut;
            }
        }

        public void Cancel()
        {
            if (!IsFinished) Phase = CapturePhase.Cancelled;
        }

        public void Observe(LiveSlot live, DateTime now)
        {
            if (IsFinished) return;
            Tick(now);
            if (IsFinished) return;

            switch (Phase)
            {
                case CapturePhase.Baseline: AccumulateBaseline(live); break;
                case CapturePhase.Tracking: TrackAxis(live); break;
                case CapturePhase.Active: LookForInput(live); break;
            }
        }

        // ---------- Phase 1 ----------

        private void AccumulateBaseline(LiveSlot live)
        {
            var b = GetOrCreateBaseline(live.Slot, live);

            b.Buttons |= live.Buttons;

            if (live.Axes != null)
            {
                for (int i = 0; i < live.Axes.Length; i++)
                {
                    int value = live.Axes[i];
                    int existing;
                    if (!b.AxisMin.TryGetValue(i, out existing) || value < existing) b.AxisMin[i] = value;
                    if (!b.AxisMax.TryGetValue(i, out existing) || value > existing) b.AxisMax[i] = value;
                }
            }

            // A direction held through the baseline window becomes the resting
            // state, so the user has to move to a different one to trigger.
            if (live.HasHat) b.Hat = live.Hat;

            // Keys held during baseline are whitelisted, so lifting and
            // re-pressing to confirm a bind doesn't latch the wrong one.
            if (live.Keys != null)
            {
                foreach (var key in live.Keys.Where(k => k > 0)) b.Keys.Add(key);
            }
        }

        private Baseline GetOrCreateBaseline(int slot, LiveSlot live)
        {
            Baseline b;
            if (_baselines.TryGetValue(slot, out b)) return b;

            b = new Baseline { Buttons = live.Buttons, Hat = live.HasHat ? live.Hat : null };
            if (live.Axes != null)
            {
                for (int i = 0; i < live.Axes.Length; i++)
                {
                    b.AxisMin[i] = live.Axes[i];
                    b.AxisMax[i] = live.Axes[i];
                }
            }
            _baselines[slot] = b;
            return b;
        }

        // ---------- Phase 2 ----------

        private void LookForInput(LiveSlot live)
        {
            var device = _lookupDevice(live.Slot);
            if (device == null || !device.Connected) return;

            Baseline b;
            if (!_baselines.TryGetValue(live.Slot, out b))
            {
                // The device said nothing for the whole baseline window —
                // keyboards and jitter-free gamepads report only on change, so
                // they were never sampled. Seed an at-rest baseline from this
                // first frame, otherwise the input that triggered it would be
                // compared against nothing and silently ignored until timeout.
                b = new Baseline { Buttons = 0, Hat = null };
                if (live.Axes != null)
                {
                    for (int i = 0; i < live.Axes.Length; i++)
                    {
                        b.AxisMin[i] = live.Axes[i];
                        b.AxisMax[i] = live.Axes[i];
                    }
                }
                _baselines[live.Slot] = b;
            }

            // A button is unambiguous — commit on the lowest newly-set bit.
            uint newBits = live.Buttons & ~b.Buttons;
            if (newBits != 0)
            {
                int bit = 0;
                while ((newBits & (1u << bit)) == 0) bit++;
                Finish(Bind(device, InputType.Button, bit));
                return;
            }

            // Only a move *into* a direction counts; returning to centre doesn't.
            if (live.HasHat && live.Hat.HasValue && live.Hat != b.Hat)
            {
                Finish(Bind(device, InputType.Hat, live.Hat.Value));
                return;
            }

            if (live.Keys != null)
            {
                // Modifiers are ordinary scancodes here, so binding LShift works.
                foreach (var code in live.Keys)
                {
                    if (code == 0 || b.Keys.Contains(code)) continue;
                    Finish(Bind(device, InputType.Key, code));
                    return;
                }
            }

            if (live.Axes == null) return;

            for (int i = 0; i < live.Axes.Length; i++)
            {
                int value = live.Axes[i];
                int lo, hi;
                if (!b.AxisMin.TryGetValue(i, out lo)) lo = value;
                if (!b.AxisMax.TryGetValue(i, out hi)) hi = value;

                double mid = (lo + hi) / 2.0;
                double noise = hi - lo;
                double threshold = Math.Max(noise * TriggerNoiseMultiple, MinimumTrigger);

                if (Math.Abs(value - mid) <= threshold) continue;

                _tracking = new Tracking
                {
                    DeviceSlot = live.Slot,
                    AxisIndex = i,
                    Vid = device.Vid,
                    Pid = device.Pid,
                    BaselineMid = mid,
                    Noise = noise,
                    PeakHigh = value,
                    PeakLow = value,
                    TriggerThreshold = threshold,
                };
                Phase = CapturePhase.Tracking;
                return;
            }
        }

        // ---------- Phase 3 ----------

        private void TrackAxis(LiveSlot live)
        {
            var t = _tracking;
            if (t == null || live.Slot != t.DeviceSlot) return;
            if (live.Axes == null || t.AxisIndex >= live.Axes.Length) return;

            int value = live.Axes[t.AxisIndex];
            if (value > t.PeakHigh) t.PeakHigh = value;
            if (value < t.PeakLow) t.PeakLow = value;

            double returnBand = Math.Max(t.Noise * ReturnNoiseMultiple, MinimumReturnBand);
            double travel = Math.Max(t.PeakHigh - t.BaselineMid, t.BaselineMid - t.PeakLow);

            // Both conditions matter: real travel proves a press happened, and
            // the return proves it ended. Without the travel check, jitter that
            // never left the resting band would commit a zero-range binding.
            bool pressed = travel > t.TriggerThreshold * PressedTravelMultiple;
            if (pressed && Math.Abs(value - t.BaselineMid) <= returnBand)
            {
                Finish(BuildAxisBinding());
            }
        }

        /// <summary>
        /// Turns the accumulated travel into a calibrated axis binding. The
        /// direction is whichever side moved further from rest: a pedal that
        /// falls when pressed gets its range reversed and invert set, so the
        /// scaled output still ramps 0 → 65535 under the foot.
        /// </summary>
        private InputBinding BuildAxisBinding()
        {
            var t = _tracking;
            if (t == null) return null;
            _tracking = null;

            double highDelta = t.PeakHigh - t.BaselineMid;
            double lowDelta = t.BaselineMid - t.PeakLow;
            bool ascending = highDelta >= lowDelta;

            int rawMin, rawMax;
            bool invert;

            if (ascending)
            {
                rawMin = Math.Max(0, (int)Math.Floor(t.BaselineMid - t.Noise));
                rawMax = Math.Max(t.PeakHigh, rawMin + MinimumRange);
                invert = false;
            }
            else
            {
                rawMin = Math.Max(0, (int)Math.Floor((double)t.PeakLow));
                rawMax = Math.Max((int)Math.Floor(t.BaselineMid + t.Noise), rawMin + MinimumRange);
                invert = true;
            }

            int range = rawMax - rawMin;
            int scaledNoise = range > 0 ? (int)Math.Floor(t.Noise * 65535.0 / range) : 0;

            return new InputBinding
            {
                Vid = t.Vid,
                Pid = t.Pid,
                Type = InputType.Axis,
                Index = t.AxisIndex,
                RawMin = rawMin,
                RawMax = rawMax,
                DeadzoneLow = Math.Min(scaledNoise * DeadzoneNoiseMultiple, MaximumDeadzone),
                DeadzoneHigh = 65535,
                Threshold = 32768,
                Invert = invert,
            };
        }

        private static InputBinding Bind(DeviceSlot device, InputType type, int index) =>
            new InputBinding
            {
                Vid = device.Vid,
                Pid = device.Pid,
                Type = type,
                Index = index,
            };

        private void Finish(InputBinding binding, bool fromDeadline = false)
        {
            if (binding == null)
            {
                Phase = CapturePhase.TimedOut;
                return;
            }

            Result = new CaptureResult
            {
                Channel = Channel,
                Slot = BindingSlot,
                Binding = binding,
                FromDeadline = fromDeadline,
            };
            Phase = CapturePhase.Committed;
        }
    }
}
