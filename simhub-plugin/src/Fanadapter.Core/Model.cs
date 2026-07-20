using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fanadapter.Core
{
    /// <summary>
    /// C# mirror of the firmware Config schema (firmware/mapping.h). Property
    /// names carry explicit JSON names matching what protocol.cpp emits — the
    /// wire shape has to round-trip exactly, since webconfig and this plugin
    /// both write the same firmware.
    /// </summary>
    public static class Schema
    {
        public const int MaxBindingsPerChannel = 4;

        /// <summary>Config version the firmware currently emits (mapping.h CONFIG_VERSION).</summary>
        public const int ConfigVersion = 3;

        public static readonly string[] GearKeys =
        {
            "gear_R", "gear_1", "gear_2", "gear_3", "gear_4",
            "gear_5", "gear_6", "gear_7", "gear_N",
        };

        public static readonly string[] AxisChannelKeys =
        {
            "handbrake", "throttle", "brake", "clutch",
        };

        public static bool IsGearKey(string channel) => Array.IndexOf(GearKeys, channel) >= 0;
    }

    public enum InputType
    {
        None,
        Button,
        Axis,
        Hat,
        Key,
    }

    public enum GearMode
    {
        Hold,
        Latch,
    }

    public enum ShiftDirection
    {
        Up,
        Down,
    }

    public class InputBinding
    {
        [JsonProperty("vid")] public int Vid { get; set; }
        [JsonProperty("pid")] public int Pid { get; set; }

        [JsonProperty("type")]
        [JsonConverter(typeof(InputTypeConverter))]
        public InputType Type { get; set; } = InputType.None;

        /// <summary>Button bit 0-31, axis index, hat direction 0-7, or HID keyboard scancode.</summary>
        [JsonProperty("index")] public int Index { get; set; }

        [JsonProperty("threshold")] public int Threshold { get; set; } = 32768;
        [JsonProperty("rawMin")] public int RawMin { get; set; }
        [JsonProperty("rawMax")] public int RawMax { get; set; }
        [JsonProperty("deadzoneLow")] public int DeadzoneLow { get; set; }
        [JsonProperty("deadzoneHigh")] public int DeadzoneHigh { get; set; } = 65535;
        [JsonProperty("invert")] public bool Invert { get; set; }

        public bool IsEmpty => Type == InputType.None;

        /// <summary>
        /// An unbound slot. RawMin == RawMax == 0 selects the firmware's
        /// pass-through path in scale_axis(), so a newly bound axis shows real
        /// movement before it has been calibrated.
        /// </summary>
        public static InputBinding None() => new InputBinding();

        public InputBinding Clone() => (InputBinding)MemberwiseClone();
    }

    public class GearDac
    {
        [JsonProperty("x")] public int X { get; set; }
        [JsonProperty("y")] public int Y { get; set; }
    }

    public class Config
    {
        [JsonProperty("version")] public int Version { get; set; }
        [JsonProperty("pulseMs")] public int PulseMs { get; set; }

        [JsonProperty("gearMode")]
        [JsonConverter(typeof(GearModeConverter))]
        public GearMode GearMode { get; set; } = GearMode.Hold;

        [JsonProperty("max_bindings_per_channel")]
        public int? MaxBindingsPerChannel { get; set; }

        [JsonProperty("gear")]
        public Dictionary<string, ChannelBindings> Gear { get; set; } = new Dictionary<string, ChannelBindings>();

        [JsonProperty("gearOut")]
        public Dictionary<string, GearDac> GearOut { get; set; } = new Dictionary<string, GearDac>();

        [JsonProperty("shift_up")] public ChannelBindings ShiftUp { get; set; } = new ChannelBindings();
        [JsonProperty("shift_down")] public ChannelBindings ShiftDown { get; set; } = new ChannelBindings();
        [JsonProperty("handbrake")] public ChannelBindings Handbrake { get; set; } = new ChannelBindings();
        [JsonProperty("throttle")] public ChannelBindings Throttle { get; set; } = new ChannelBindings();
        [JsonProperty("brake")] public ChannelBindings Brake { get; set; } = new ChannelBindings();
        [JsonProperty("clutch")] public ChannelBindings Clutch { get; set; } = new ChannelBindings();

        /// <summary>
        /// Bindings for a channel key, always padded to MaxBindingsPerChannel.
        /// Unknown keys yield an all-empty set rather than throwing, so a
        /// firmware that drops a channel doesn't take the UI down with it.
        /// </summary>
        public ChannelBindings GetChannel(string channel)
        {
            ChannelBindings found;
            if (Schema.IsGearKey(channel))
            {
                Gear.TryGetValue(channel, out found);
            }
            else
            {
                switch (channel)
                {
                    case "shift_up": found = ShiftUp; break;
                    case "shift_down": found = ShiftDown; break;
                    case "handbrake": found = Handbrake; break;
                    case "throttle": found = Throttle; break;
                    case "brake": found = Brake; break;
                    case "clutch": found = Clutch; break;
                    default: found = null; break;
                }
            }

            if (found == null)
            {
                found = new ChannelBindings();
                if (Schema.IsGearKey(channel)) Gear[channel] = found;
            }

            found.Pad();
            return found;
        }

        public GearDac GetGearDac(string gearKey)
        {
            GearDac dac;
            if (!GearOut.TryGetValue(gearKey, out dac))
            {
                dac = new GearDac();
                GearOut[gearKey] = dac;
            }
            return dac;
        }
    }

    /// <summary>
    /// The up-to-four binding slots on one channel. Buttons OR together across
    /// slots, axes MAX together — the firmware does that aggregation; this type
    /// just carries the slots.
    /// </summary>
    [JsonConverter(typeof(ChannelBindingsConverter))]
    public class ChannelBindings
    {
        public List<InputBinding> Slots { get; } = new List<InputBinding>();

        public InputBinding this[int slot]
        {
            get { Pad(); return Slots[slot]; }
        }

        public int Count => Slots.Count;

        public void Pad()
        {
            while (Slots.Count < Schema.MaxBindingsPerChannel) Slots.Add(InputBinding.None());
        }

        /// <summary>First unused slot, or -1 when every slot is taken.</summary>
        public int FirstEmptySlot()
        {
            Pad();
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].IsEmpty) return i;
            }
            return -1;
        }

        public bool HasAny()
        {
            return Slots.Any(s => !s.IsEmpty);
        }
    }

    // ---------- Converters ----------

    /// <summary>
    /// Accepts both channel shapes the firmware has emitted: the current array
    /// of four slots, and the single-object form from firmware v0.2.0. Keeping
    /// the fallback means the plugin still renders against an adapter that
    /// hasn't been reflashed — same leniency webconfig's types.ts has.
    /// </summary>
    internal class ChannelBindingsConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(ChannelBindings);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var result = new ChannelBindings();
            var token = JToken.Load(reader);

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in (JArray)token)
                {
                    result.Slots.Add(item.ToObject<InputBinding>(serializer));
                }
            }
            else if (token.Type == JTokenType.Object && token["type"] != null)
            {
                result.Slots.Add(token.ToObject<InputBinding>(serializer));
            }

            result.Pad();
            return result;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var bindings = (ChannelBindings)value;
            bindings.Pad();
            serializer.Serialize(writer, bindings.Slots);
        }
    }

    internal class InputTypeConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(InputType);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var s = reader.Value as string;
            switch (s)
            {
                case "button": return InputType.Button;
                case "axis": return InputType.Axis;
                case "hat": return InputType.Hat;
                case "key": return InputType.Key;
                default: return InputType.None;
            }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(Wire((InputType)value));
        }

        public static string Wire(InputType t)
        {
            switch (t)
            {
                case InputType.Button: return "button";
                case InputType.Axis: return "axis";
                case InputType.Hat: return "hat";
                case InputType.Key: return "key";
                default: return "none";
            }
        }
    }

    internal class GearModeConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(GearMode);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            return (reader.Value as string) == "latch" ? GearMode.Latch : GearMode.Hold;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue((GearMode)value == GearMode.Latch ? "latch" : "hold");
        }
    }

    // ---------- Protocol payloads ----------

    public class VersionInfo
    {
        [JsonProperty("fw")] public string Firmware { get; set; }
        [JsonProperty("ver")] public string Version { get; set; }
        [JsonProperty("protocol")] public int Protocol { get; set; }
        [JsonProperty("max_bindings_per_channel")] public int? MaxBindingsPerChannel { get; set; }

        /// <summary>
        /// The direct-output command set (set_gear / set_outputs / pulse_shift /
        /// release_outputs) exists only in the STM32 firmware — the Teensy
        /// dispatcher answers unknown_cmd. Everything that drives outputs from
        /// the PC has to check this first.
        /// </summary>
        public bool SupportsDirectOutput =>
            Firmware != null && Firmware.IndexOf("stm32", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public class DeviceSlot
    {
        [JsonProperty("slot")] public int Slot { get; set; }
        [JsonProperty("connected")] public bool Connected { get; set; }
        [JsonProperty("vid")] public int Vid { get; set; }
        [JsonProperty("pid")] public int Pid { get; set; }
        [JsonProperty("manufacturer")] public string Manufacturer { get; set; }
        [JsonProperty("product")] public string Product { get; set; }
        [JsonProperty("axis_count")] public int AxisCount { get; set; }
        [JsonProperty("button_count")] public int ButtonCount { get; set; }
        [JsonProperty("has_hat")] public bool HasHat { get; set; }
        [JsonProperty("has_keyboard")] public bool HasKeyboard { get; set; }

        public string DisplayName
        {
            get
            {
                var name = string.Join(" ", new[] { Manufacturer, Product }
                    .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                return name.Length > 0 ? name : string.Format("{0:X4}:{1:X4}", Vid, Pid);
            }
        }
    }

    public class OutputsState
    {
        [JsonProperty("gear")] public string Gear { get; set; }
        [JsonProperty("shift_up")] public bool ShiftUp { get; set; }
        [JsonProperty("shift_down")] public bool ShiftDown { get; set; }
        [JsonProperty("throttle")] public int Throttle { get; set; }
        [JsonProperty("brake")] public int Brake { get; set; }
        [JsonProperty("clutch")] public int Clutch { get; set; }
        [JsonProperty("handbrake")] public int Handbrake { get; set; }
    }

    public class PedalsStatus
    {
        [JsonProperty("state")] public string State { get; set; }
        [JsonProperty("throttle")] public int Throttle { get; set; }
        [JsonProperty("brake")] public int Brake { get; set; }
        [JsonProperty("clutch")] public int Clutch { get; set; }
        [JsonProperty("handbrake")] public int Handbrake { get; set; }
    }

    /// <summary>One slot's live input snapshot, from the firmware's `live` event.</summary>
    public class LiveSlot
    {
        public int Slot { get; set; }
        public uint Buttons { get; set; }
        public int[] Axes { get; set; } = new int[0];

        /// <summary>0-7 = direction, null = released, absent when the device has no hat.</summary>
        public int? Hat { get; set; }
        public bool HasHat { get; set; }

        /// <summary>Pressed HID scancodes, zero-padded by the firmware. Null for non-keyboards.</summary>
        public int[] Keys { get; set; }
    }
}
