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
    /// One pedal axis, sourced from a host property or SimHub's native axis
    /// picker (including Control Mapper roles). The picker assignment is stored
    /// as plain names so Core keeps no dependency on SimHub's assemblies.
    /// </summary>
    public class AxisSource
    {
        public string PropertyName { get; set; }

        /// <summary>Opt-in; older settings keep their existing property source.</summary>
        public bool UseSimHubAxis { get; set; }

        public string AxisName { get; set; }

        /// <summary>Named SimHub AxisMovement value, avoiding a numeric enum mirror.</summary>
        public string AxisMovement { get; set; } = "MinToMax";

        /// <summary>
        /// Raw range of the source property. Defaults to 0..100 because that is
        /// what SimHub's own axis properties use; a 0..1 or 0..65535 source just
        /// needs these changed.
        /// </summary>
        public double InputMin { get; set; }
        public double InputMax { get; set; } = 100;

        public bool Invert { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(UseSimHubAxis ? AxisName : PropertyName);

        /// <summary>
        /// Maps a source value onto the firmware's 0..65535 axis range. Native
        /// picker values are already processed into 0..1; property calibration
        /// remains saved but does not apply to them a second time.
        /// Returns null when the value can't be read as a number, so the caller
        /// can leave the channel alone instead of slamming a pedal to zero
        /// because a property name was mistyped.
        /// </summary>
        public int? Scale(object rawValue)
        {
            if (rawValue == null) return null;

            double min = UseSimHubAxis ? 0 : InputMin;
            double max = UseSimHubAxis ? 1 : InputMax;

            double value;
            var flag = rawValue as bool?;
            if (flag.HasValue)
            {
                // A button bound to a pedal channel is legitimate — full travel
                // or nothing, matching how the firmware treats a button driving
                // an axis channel.
                value = flag.Value ? max : min;
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

            double span = max - min;
            double t = Math.Abs(span) < double.Epsilon ? 0 : (value - min) / span;

            if (t < 0) t = 0;
            else if (t > 1) t = 1;

            if (!UseSimHubAxis && Invert) t = 1 - t;

            return (int)Math.Round(t * 65535.0);
        }
    }
}
