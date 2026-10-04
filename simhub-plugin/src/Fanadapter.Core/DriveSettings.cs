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
        /// At least one pedal has a source property. Streaming without one sends
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
    /// One pedal axis, sourced from a host-application property. Any property
    /// works — a controller axis published by SimHub's input plugins, or an
    /// expression the user built — which is why this stores a name rather than
    /// a device and axis index.
    /// </summary>
    public class AxisSource
    {
        public string PropertyName { get; set; }

        /// <summary>
        /// Raw range of the source property. Defaults to 0..100 because that is
        /// what SimHub's own axis properties use; a 0..1 or 0..65535 source just
        /// needs these changed.
        /// </summary>
        public double InputMin { get; set; }
        public double InputMax { get; set; } = 100;

        public bool Invert { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(PropertyName);

        /// <summary>
        /// Maps a raw property value onto the firmware's 0..65535 axis range.
        /// Returns null when the value can't be read as a number, so the caller
        /// can leave the channel alone instead of slamming a pedal to zero
        /// because a property name was mistyped.
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
                value = flag.Value ? InputMax : InputMin;
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

            double span = InputMax - InputMin;
            double t = Math.Abs(span) < double.Epsilon ? 0 : (value - InputMin) / span;

            if (t < 0) t = 0;
            else if (t > 1) t = 1;

            if (Invert) t = 1 - t;

            return (int)Math.Round(t * 65535.0);
        }
    }
}
