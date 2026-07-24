using System;
using System.Collections.Generic;

namespace Fanadapter.Core
{
    /// <summary>
    /// Listen-style auto-detect for the PC pedal path: press Detect, press the
    /// pedal, and this works out WHICH host-application property it is, its
    /// raw range, and its direction — the three things AxisSource needs.
    ///
    /// The adapter-side CaptureEngine watches one device's axis frames; this
    /// one watches EVERY property the host publishes, because on the PC side
    /// "which property is my brake" is exactly the unknown. Same three-phase
    /// shape as webconfig's Listen (baseline → trigger → track-until-release),
    /// adapted to properties of arbitrary, unknown scale:
    ///
    ///  * Baseline (~0.6 s): per-property min/max builds a noise floor. A
    ///    property already moving is excluded outright — that silently drops
    ///    live game telemetry (speed, rpm, clocks), which would otherwise
    ///    false-trigger the instant a session is running.
    ///  * Listening: trigger when a property deviates from its baseline
    ///    midpoint by max(noise × 6, a scale-aware floor). If several cross in
    ///    the same tick (mirrored properties, or telemetry reacting to the
    ///    pedal in a live session), the input-ish namespace wins, then the
    ///    largest relative deviation.
    ///  * Tracking: accumulate the latched property's travel; commit when it
    ///    returns near baseline. Direction = whichever side travelled further;
    ///    descending sets Invert, matching how AxisSource.Scale maps a
    ///    rests-high pedal.
    ///
    /// Timestamps are passed in explicitly (seconds since the capture began),
    /// so tests drive the phases without a clock.
    /// </summary>
    public class PropertyAxisDetector
    {
        private const double BaselineSeconds = 0.6;
        private const double TimeoutSeconds = 10.0;
        private const double TriggerNoiseFactor = 6.0;
        private const double ReturnNoiseFactor = 3.0;

        private sealed class Candidate
        {
            public double Min = double.MaxValue;
            public double Max = double.MinValue;
            public bool Excluded;

            public double Mid => (Min + Max) / 2.0;
            public double Noise => Max - Min;

            // 2 % of the property's own magnitude, floored at 0.25 — small
            // enough that a normalized 0..1 axis still triggers on a quarter
            // press, large enough to ignore fractional wobble. Integer-scale
            // jitter that slips past this is caught by the minimum-travel check
            // at commit time.
            public double Floor => Math.Max(0.25, 0.02 * Math.Max(Math.Abs(Min), Math.Abs(Max)));
            public double TriggerBand => Math.Max(Noise * TriggerNoiseFactor, Floor);
            public double ReturnBand => Math.Max(Noise * ReturnNoiseFactor, Floor * 0.6);
        }

        private readonly Dictionary<string, Candidate> _candidates =
            new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        private string _latched;
        private double _peakHigh, _peakLow;
        private double _latchSeconds;
        private const double MinHoldSeconds = 0.15;

        public DetectPhase Phase { get; private set; } = DetectPhase.Baseline;

        /// <summary>Latched property once tracking starts — for status display.</summary>
        public string LatchedProperty => _latched;

        /// <summary>Set when Phase == Done.</summary>
        public AxisDetection Result { get; private set; }

        /// <summary>Set when Phase == Failed.</summary>
        public string FailureReason { get; private set; }

        /// <summary>
        /// Names still in the running after the baseline pruned the excluded
        /// ones. The caller should narrow its sampling to these once Phase
        /// leaves Baseline — reading every host property at 60 Hz is the
        /// expensive part, and most die in the baseline (non-numeric or
        /// already moving). During Tracking only the latched name matters.
        /// </summary>
        public IReadOnlyList<string> SurvivingCandidates()
        {
            if (Phase == DetectPhase.Tracking && _latched != null) return new[] { _latched };
            var names = new List<string>(_candidates.Count);
            foreach (var kv in _candidates)
            {
                if (!kv.Value.Excluded) names.Add(kv.Key);
            }
            return names;
        }

        /// <summary>
        /// Feed one sampling tick. <paramref name="seconds"/> is time since the
        /// capture began; <paramref name="sample"/> is the current numeric value
        /// of every candidate property (bools coerced to 0/1 by the caller — a
        /// button driving a pedal channel is legitimate full-travel-or-nothing).
        /// </summary>
        public void Feed(double seconds, IEnumerable<KeyValuePair<string, double>> sample)
        {
            switch (Phase)
            {
                case DetectPhase.Baseline:
                    FeedBaseline(seconds, sample);
                    break;
                case DetectPhase.Listening:
                    FeedListening(seconds, sample);
                    break;
                case DetectPhase.Tracking:
                    FeedTracking(seconds, sample);
                    break;
            }
        }

        private void FeedBaseline(double seconds, IEnumerable<KeyValuePair<string, double>> sample)
        {
            foreach (var kv in sample)
            {
                if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value)) continue;

                Candidate c;
                if (!_candidates.TryGetValue(kv.Key, out c))
                {
                    c = new Candidate();
                    _candidates[kv.Key] = c;
                }
                if (kv.Value < c.Min) c.Min = kv.Value;
                if (kv.Value > c.Max) c.Max = kv.Value;
            }

            if (seconds < BaselineSeconds) return;

            // Baseline done. A property that already exceeded its own trigger
            // band was moving on its own — running telemetry, a clock, another
            // person's wheel — and can never be a resting pedal.
            foreach (var c in _candidates.Values)
            {
                if (c.Noise > c.Floor * 2.0) c.Excluded = true;
            }
            Phase = DetectPhase.Listening;
        }

        private void FeedListening(double seconds, IEnumerable<KeyValuePair<string, double>> sample)
        {
            if (seconds > TimeoutSeconds)
            {
                Phase = DetectPhase.Failed;
                FailureReason = "nothing moved — is the controller plugged in and publishing to the host app?";
                return;
            }

            string best = null;
            Candidate bestCand = null;
            double bestScore = 0;
            bool bestIsInput = false;
            double bestValue = 0;

            foreach (var kv in sample)
            {
                Candidate c;
                if (!_candidates.TryGetValue(kv.Key, out c) || c.Excluded) continue;
                if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value)) continue;

                double deviation = Math.Abs(kv.Value - c.Mid);
                if (deviation <= c.TriggerBand) continue;

                // Ranking among simultaneous crossers: an input-namespace
                // property beats telemetry that merely reacted to the pedal
                // (pressing throttle in a live session raises rpm too); after
                // that, the biggest relative deviation wins.
                bool isInput = LooksLikeInputProperty(kv.Key);
                double score = deviation / c.TriggerBand;
                if (best == null || (isInput && !bestIsInput) || (isInput == bestIsInput && score > bestScore))
                {
                    best = kv.Key;
                    bestCand = c;
                    bestScore = score;
                    bestIsInput = isInput;
                    bestValue = kv.Value;
                }
            }

            if (best == null) return;

            _latched = best;
            _latchSeconds = seconds;
            _peakHigh = Math.Max(bestValue, bestCand.Mid);
            _peakLow = Math.Min(bestValue, bestCand.Mid);
            Phase = DetectPhase.Tracking;
        }

        private void FeedTracking(double seconds, IEnumerable<KeyValuePair<string, double>> sample)
        {
            var c = _candidates[_latched];

            foreach (var kv in sample)
            {
                if (!string.Equals(kv.Key, _latched, StringComparison.OrdinalIgnoreCase)) continue;
                if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value)) continue;

                if (kv.Value > _peakHigh) _peakHigh = kv.Value;
                if (kv.Value < _peakLow) _peakLow = kv.Value;

                if (Math.Abs(kv.Value - c.Mid) <= c.ReturnBand)
                {
                    Commit(c, seconds);
                }
                return;
            }
        }

        private void Commit(Candidate c, double seconds)
        {
            // A real press travels far past the trigger band AND takes real
            // time; a latch from one stray count fails both. Unlatch and keep
            // listening rather than committing a garbage one-count range —
            // webconfig's capture has the same hasPressed guard.
            if (_peakHigh - _peakLow < c.TriggerBand * 3.0 ||
                seconds - _latchSeconds < MinHoldSeconds)
            {
                _latched = null;
                Phase = DetectPhase.Listening;
                return;
            }

            double up = _peakHigh - c.Mid;
            double down = c.Mid - _peakLow;
            bool rising = up >= down;

            // Rising pedal: rests at baseline, presses toward peakHigh.
            // Falling pedal: rests high, presses toward peakLow — Invert makes
            // AxisSource.Scale read rest as 0 % and full press as 100 %.
            Result = new AxisDetection
            {
                PropertyName = _latched,
                InputMin = rising ? c.Mid : _peakLow,
                InputMax = rising ? _peakHigh : c.Mid,
                Invert = !rising,
            };
            Phase = DetectPhase.Done;
        }

        internal static bool LooksLikeInputProperty(string name) =>
            name != null &&
            (name.IndexOf("JoystickPlugin", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("InputStatus", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("Joypad", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public enum DetectPhase
    {
        Baseline,
        Listening,
        Tracking,
        Done,
        Failed,
    }

    /// <summary>What a successful detection measured — maps 1:1 onto AxisSource.</summary>
    public class AxisDetection
    {
        public string PropertyName { get; set; }
        public double InputMin { get; set; }
        public double InputMax { get; set; }
        public bool Invert { get; set; }
    }
}
