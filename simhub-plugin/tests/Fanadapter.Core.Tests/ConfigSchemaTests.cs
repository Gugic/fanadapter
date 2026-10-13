using System.Linq;
using Fanadapter.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fanadapter.Core.Tests
{
    public class ConfigSchemaTests
    {
        private const string V3Config = @"{
            ""version"": 3,
            ""pulseMs"": 50,
            ""gearMode"": ""latch"",
            ""max_bindings_per_channel"": 4,
            ""gear"": {
                ""gear_1"": [
                    {""vid"":1133,""pid"":49747,""type"":""button"",""index"":2,""threshold"":32768,
                     ""rawMin"":0,""rawMax"":0,""deadzoneLow"":0,""deadzoneHigh"":65535,""invert"":false}
                ]
            },
            ""gearOut"": { ""gear_1"": {""x"":2790,""y"":3430} },
            ""shift_up"": [],
            ""shift_down"": [],
            ""handbrake"": [],
            ""throttle"": [
                {""vid"":1,""pid"":2,""type"":""axis"",""index"":0,""threshold"":32768,
                 ""rawMin"":100,""rawMax"":4000,""deadzoneLow"":500,""deadzoneHigh"":65535,""invert"":true}
            ],
            ""brake"": [],
            ""clutch"": []
        }";

        [Fact]
        public void ParsesTheCurrentWireShape()
        {
            var config = JsonConvert.DeserializeObject<Config>(V3Config);

            Assert.Equal(3, config.Version);
            Assert.Equal(50, config.PulseMs);
            Assert.Equal(GearMode.Latch, config.GearMode);
            Assert.Equal(2790, config.GetGearDac("gear_1").X);

            var throttle = config.GetChannel("throttle")[0];
            Assert.Equal(InputType.Axis, throttle.Type);
            Assert.Equal(100, throttle.RawMin);
            Assert.True(throttle.Invert);
        }

        [Fact]
        public void PadsEveryChannelToFourSlots()
        {
            var config = JsonConvert.DeserializeObject<Config>(V3Config);

            Assert.Equal(Schema.MaxBindingsPerChannel, config.GetChannel("throttle").Count);
            Assert.Equal(Schema.MaxBindingsPerChannel, config.GetChannel("gear_1").Count);
            Assert.All(config.GetChannel("brake").Slots, b => Assert.Equal(InputType.None, b.Type));
        }

        [Fact]
        public void AcceptsTheLegacySingleObjectChannelShape()
        {
            // Firmware v0.2.0 sent one object per channel instead of an array.
            // Staying lenient means the plugin still renders against an adapter
            // that hasn't been reflashed.
            const string v1 = @"{
                ""version"": 1, ""pulseMs"": 50,
                ""gear"": { ""gear_2"": {""vid"":0,""pid"":0,""type"":""button"",""index"":7} },
                ""gearOut"": {},
                ""throttle"": {""vid"":0,""pid"":0,""type"":""axis"",""index"":1}
            }";

            var config = JsonConvert.DeserializeObject<Config>(v1);

            Assert.Equal(InputType.Button, config.GetChannel("gear_2")[0].Type);
            Assert.Equal(7, config.GetChannel("gear_2")[0].Index);
            Assert.Equal(InputType.Axis, config.GetChannel("throttle")[0].Type);
            Assert.Equal(Schema.MaxBindingsPerChannel, config.GetChannel("throttle").Count);
        }

        [Fact]
        public void UnknownChannelYieldsEmptySlotsRatherThanThrowing()
        {
            var config = JsonConvert.DeserializeObject<Config>(V3Config);
            var slots = config.GetChannel("gear_9");

            Assert.Equal(Schema.MaxBindingsPerChannel, slots.Count);
            Assert.False(slots.HasAny());
        }

        [Fact]
        public void BindingRoundTripsThroughTheWireFormat()
        {
            var original = new InputBinding
            {
                Vid = 0x046D,
                Pid = 0xC29B,
                Type = InputType.Hat,
                Index = 5,
                Threshold = 1000,
                RawMin = 10,
                RawMax = 4000,
                DeadzoneLow = 20,
                DeadzoneHigh = 60000,
                Invert = true,
            };

            var json = JObject.FromObject(original);

            // Field names are a firmware contract, not an implementation detail.
            Assert.Equal("hat", json.Value<string>("type"));
            Assert.Equal(0x046D, json.Value<int>("vid"));
            Assert.Equal(60000, json.Value<int>("deadzoneHigh"));

            var back = json.ToObject<InputBinding>();
            Assert.Equal(InputType.Hat, back.Type);
            Assert.Equal(5, back.Index);
            Assert.True(back.Invert);
        }

        [Fact]
        public void UnknownInputTypeDegradesToNone()
        {
            var b = JObject.Parse("{\"type\":\"encoder\",\"index\":1}").ToObject<InputBinding>();
            Assert.Equal(InputType.None, b.Type);
        }

        [Fact]
        public void FirstEmptySlotFindsTheNextFreeBinding()
        {
            var channel = new ChannelBindings();
            channel.Pad();
            Assert.Equal(0, channel.FirstEmptySlot());

            channel[0].Type = InputType.Button;
            Assert.Equal(1, channel.FirstEmptySlot());

            foreach (var slot in channel.Slots) slot.Type = InputType.Button;
            Assert.Equal(-1, channel.FirstEmptySlot());
        }

        [Theory]
        [InlineData("fanadapter-stm32", true)]
        [InlineData("unrecognized-firmware", false)]
        public void DirectOutputSupportIsDetectedFromTheFirmwareName(string fw, bool expected)
        {
            // Only identified STM32 firmware exposes the PC direct-output features.
            Assert.Equal(expected, new VersionInfo { Firmware = fw }.SupportsDirectOutput);
        }
    }

    public class PortLocatorTests
    {
        [Theory]
        [InlineData("USB-SERIAL CH340K (COM16)", "COM16")]
        [InlineData("USB Serial Device (COM3)", "COM3")]
        [InlineData("Communications Port (COM1)", "COM1")]
        [InlineData("Some device with no port", null)]
        public void ExtractsThePortNameFromAFriendlyName(string friendly, string expected)
        {
            Assert.Equal(expected, PortLocator.ExtractPortName(friendly));
        }

        [Theory]
        [InlineData(0x1209, 0xFA00, true)]  // STM32 native CDC
        [InlineData(0x16C0, 0x0483, false)] // Unrecognized USB identity
        [InlineData(0x1A86, 0x7522, false)] // Generic CH340K bridge
        [InlineData(0x1209, 0x0001, false)] // pid.codes, but not ours
        [InlineData(0x0403, 0x6001, false)] // Generic FTDI bridge
        [InlineData(0x0483, 0xDF11, false)] // ROM DFU is not the app's serial port
        public void RecognisesKnownAdapterIdentities(int vid, int pid, bool expected)
        {
            Assert.Equal(expected, PortLocator.IsKnown(vid, pid));
        }

        [Fact]
        public void OnlyExactAdapterIdentitiesAreOfferedRegardlessOfTheName()
        {
            var genericLabel = new SerialPortInfo
            {
                PortName = "COM3", Description = "USB Serial Device (COM3)", Vid = 0x1209, Pid = 0xFA00
            };
            var olderLabel = new SerialPortInfo
            {
                PortName = "COM4", Description = "Fanadapter STM32 enumerator", Vid = 0x1209, Pid = 0xFA00
            };
            var unrelated = new SerialPortInfo
            {
                PortName = "COM5", Description = "Fanadapter v0.6.1 (COM5)", Vid = 0x1A86, Pid = 0x7522,
                IsLikelyAdapter = true
            };
            var unidentified = new SerialPortInfo { PortName = "COM6", IsLikelyAdapter = true };

            var offered = PortLocator.FilterAdapters(new[] { unrelated, unidentified, olderLabel, genericLabel });

            Assert.Equal(new[] { genericLabel, olderLabel }, offered);
        }

        [Theory]
        [InlineData("USB Serial Device (COM3)", "Fanadapter v0.6.1", "Fanadapter v0.6.1 (COM3)")]
        [InlineData("USB Serial Device (COM3)", "Fanadapter STM32 enumerator", "Fanadapter STM32 enumerator (COM3)")]
        [InlineData("Fanadapter v0.6.1 (COM3)", null, "Fanadapter v0.6.1 (COM3)")]
        [InlineData("USB Serial Device (COM3)", null, "Fanadapter (COM3)")]
        [InlineData("USB Serial Device (COM3)", "USB Serial Device", "Fanadapter (COM3)")]
        public void ShowsTheUsbProductNameOrAVerifiedAdapterFallback(string friendly, string bus, string expected)
        {
            Assert.Equal(expected, PortLocator.AdapterDescription("COM3", friendly, bus));
        }
    }
}
