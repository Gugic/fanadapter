namespace Fanadapter.Core
{
    /// <summary>
    /// C# mirror of the firmware's <c>scale_axis()</c> (firmware/mapping.cpp,
    /// firmware-stm32/src/mapping.c) and of webconfig's scaleAxis.ts.
    ///
    /// Used only to preview what the firmware will do while the user drags the
    /// calibration sliders — the firmware still owns the real value. That makes
    /// divergence silent and confusing, which is why AGENTS.md tracks this as a
    /// cross-file invariant and why ScaleAxisTests exists.
    /// </summary>
    public static class ScaleAxis
    {
        public static ushort Apply(int raw, InputBinding b)
        {
            uint scaled;
            if (b.RawMin == 0 && b.RawMax == 0)
            {
                // Uncalibrated pass-through: a freshly bound axis shows movement
                // instead of being scaled against a bogus upper bound.
                scaled = (uint)raw;
            }
            else if (raw <= b.RawMin)
            {
                scaled = 0;
            }
            else if (raw >= b.RawMax)
            {
                scaled = 65535;
            }
            else
            {
                scaled = (uint)(raw - b.RawMin) * 65535u / (uint)(b.RawMax - b.RawMin);
            }

            if (b.Invert) scaled = 65535u - scaled;
            if (scaled <= b.DeadzoneLow) return 0;
            if (scaled >= b.DeadzoneHigh) return 65535;
            return (ushort)scaled;
        }

        public static bool EvalButton(int raw, InputBinding b) => Apply(raw, b) >= b.Threshold;
    }
}
