using System;

namespace Fanadapter.Core
{
    /// <summary>
    /// How PC-attached pedals reach the wheelbase. Buttons and gears don't
    /// appear here — those are bound through SimHub's own Controls system,
    /// which stores its mappings itself.
    ///
    /// Lives in Core rather than next to the plugin because it is plain data
    /// plus one mapping function, and that function deserves tests.
    /// </summary>
    public class DriveSettings
    {
        /// <summary>
        /// Whether the user wants pedals driven from this PC. A *desired* state,
        /// not a live one: it is persisted, and every successful connect
        /// re-applies it (see DriveController.ResumeIfEnabled). SimHub rebuilds
        /// its plugins on every game change, so without that the choice would
        /// quietly lapse each time the user switched games.
        /// </summary>
        public bool AxisStreamingEnabled { get; set; }

        public AxisSource Throttle { get; set; } = new AxisSource();
        public AxisSource Brake { get; set; } = new AxisSource();
        public AxisSource Clutch { get; set; } = new AxisSource();
        public AxisSource Handbrake { get; set; } = new AxisSource();

        /// <summary>
        /// Every pedal channel, in the order the firmware's axis commands take
        /// them. Null-tolerant on purpose: these come back from a deserialised
        /// settings blob, which can carry an explicit null for a channel.
        /// </summary>
        public AxisSource[] AllAxes() => new[] { Throttle, Brake, Clutch, Handbrake };

        /// <summary>
        /// At least one pedal has a source. Streaming without one sends
        /// nothing at all, so resuming into that state would show "driving" over
        /// a link carrying no pedal data.
        /// </summary>
        public bool HasConfiguredAxis()
        {
            foreach (var axis in AllAxes())
            {
                if (axis != null && axis.IsConfigured) return true;
            }
            return false;
        }

        public AxisSource For(string channel)
        {
            switch (channel)
            {
                case "throttle": return Throttle;
                case "brake": return Brake;
                case "clutch": return Clutch;
                case "handbrake": return Handbrake;
                default: throw new ArgumentOutOfRangeException(nameof(channel), channel, "not an axis channel");
            }
        }
    }

    /// <summary>
    /// One pedal axis, selected with SimHub's native axis picker (including
    /// Control Mapper roles). The picker assignment is stored
    /// as plain names so Core keeps no dependency on SimHub's assemblies.
    /// </summary>
    public class AxisSource
    {
        public string AxisName { get; set; }

        /// <summary>Named SimHub AxisMovement value, avoiding a numeric enum mirror.</summary>
        public string AxisMovement { get; set; } = "MinToMax";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(AxisName);

        /// <summary>
        /// Maps the picker's processed 0..1 value onto the firmware's 0..65535 range.
        /// Returns null when the value can't be read as a number, so the caller
        /// can leave the channel alone instead of slamming a pedal to zero
        /// because the source is unavailable.
        /// </summary>
        public int? Scale(object rawValue)
        {
            if (rawValue == null) return null;

            double value;
            var flag = rawValue as bool?;
            if (flag.HasValue)
            {
                // A button bound to a pedal channel is legitimate — full travel
                // or nothing, matching how the firmware treats a button driving
                // an axis channel.
                value = flag.Value ? 1 : 0;
            }
            else
            {
                try
                {
                    value = Convert.ToDouble(rawValue);
                }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
                {
                    return null;
                }
            }

            if (double.IsNaN(value) || double.IsInfinity(value)) return null;

            if (value < 0) value = 0;
            else if (value > 1) value = 1;

            return (int)Math.Round(value * 65535.0);
        }
    }
}
