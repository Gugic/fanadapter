using Fanadapter.Core;
using Newtonsoft.Json;
using Xunit;

namespace Fanadapter.Core.Tests
{
    public class AxisSourceTests
    {
        [Theory]
        [InlineData(0, 0)]
        [InlineData(0.5, 32768)]
        [InlineData(1, 65535)]
        [InlineData(-0.2, 0)]
        [InlineData(1.8, 65535)]
        public void MapsProcessedUnitRangeOntoFullTravel(double raw, int expected)
        {
            Assert.Equal(expected, new AxisSource().Scale(raw));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(0.3435, 32768)]
        [InlineData(0.687, 65535)]
        [InlineData(0.9, 65535)]
        public void LowerFullTravelRequiresLessInputForFullOutput(double raw, int expected)
        {
            Assert.Equal(expected, new AxisSource { InputMaxPercent = 68.7 }.Scale(raw));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(0.05, 0)]
        [InlineData(0.25, 32768)]
        [InlineData(0.45, 65535)]
        [InlineData(1, 65535)]
        public void ReleasedEndpointRemovesIdleTravel(double raw, int expected)
        {
            var source = new AxisSource { InputMinPercent = 5, InputMaxPercent = 45 };
            Assert.Equal(expected, source.Scale(raw));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(70, 68.7)]
        [InlineData(-1, 100)]
        [InlineData(0, 101)]
        [InlineData(double.NaN, 100)]
        [InlineData(0, double.PositiveInfinity)]
        public void InvalidRangesSendNoValue(double min, double max)
        {
            var source = new AxisSource { InputMinPercent = min, InputMaxPercent = max };
            Assert.False(source.HasValidRange());
            Assert.Null(source.Scale(0.5));
        }

        [Fact]
        public void BooleanSourcesGiveFullOrNoTravel()
        {
            var source = new AxisSource();
            Assert.Equal(65535, source.Scale(true));
            Assert.Equal(0, source.Scale(false));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not a number")]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void UnreadableValuesReturnNullSoTheChannelIsLeftAlone(object raw)
        {
            Assert.Null(new AxisSource().Scale(raw));
        }

        [Fact]
        public void NumericStringsAreStillAccepted()
        {
            Assert.Equal(32768, new AxisSource().Scale("0.5"));
        }

        [Fact]
        public void IsConfiguredIgnoresWhitespaceOnlyNames()
        {
            Assert.False(new AxisSource().IsConfigured);
            Assert.False(new AxisSource { AxisName = "   " }.IsConfigured);
            Assert.True(new AxisSource { AxisName = "ControlMapperPlugin.Throttle" }.IsConfigured);
        }

        [Fact]
        public void ChannelLookupCoversEveryAxisTheFirmwareAccepts()
        {
            var drive = new DriveSettings();
            foreach (var channel in Schema.AxisChannelKeys)
                Assert.NotNull(drive.For(channel));
        }

        [Fact]
        public void HasConfiguredAxisIsFalseUntilSomePedalHasASource()
        {
            var drive = new DriveSettings();
            Assert.False(drive.HasConfiguredAxis());
            drive.Clutch.AxisName = "ControlMapperPlugin.Clutch";
            Assert.True(drive.HasConfiguredAxis());
        }

        [Fact]
        public void HasConfiguredAxisSurvivesAChannelDeserialisedAsNull()
        {
            var drive = new DriveSettings { Throttle = null, Brake = null, Clutch = null, Handbrake = null };
            Assert.False(drive.HasConfiguredAxis());
            drive.Brake = new AxisSource { AxisName = "ControlMapperPlugin.Brake" };
            Assert.True(drive.HasConfiguredAxis());
        }

        [Fact]
        public void EachPedalAssignmentAndDirectionPersistIndependently()
        {
            var drive = new DriveSettings
            {
                Throttle = new AxisSource { AxisName = "JoystickPlugin.Throttle" },
                Brake = new AxisSource
                {
                    AxisName = "ControlMapperPlugin.Brake", AxisMovement = "MaxToMin",
                    InputMinPercent = 5, InputMaxPercent = 68.7,
                },
            };
            var restored = JsonConvert.DeserializeObject<DriveSettings>(JsonConvert.SerializeObject(drive));
            Assert.Equal("JoystickPlugin.Throttle", restored.Throttle.AxisName);
            Assert.Equal("MinToMax", restored.Throttle.AxisMovement);
            Assert.Equal("ControlMapperPlugin.Brake", restored.Brake.AxisName);
            Assert.Equal("MaxToMin", restored.Brake.AxisMovement);
            Assert.Equal(5, restored.Brake.InputMinPercent);
            Assert.Equal(68.7, restored.Brake.InputMaxPercent);
            Assert.Equal(0, restored.Throttle.InputMinPercent);
            Assert.Equal(100, restored.Throttle.InputMaxPercent);
            Assert.False(restored.Clutch.IsConfigured);
        }

        [Fact]
        public void RemovedPropertySettingsCannotConfigureOrRecalibrateANativeAxis()
        {
            var source = JsonConvert.DeserializeObject<AxisSource>(
                "{\"PropertyName\":\"Old.Brake\",\"InputMin\":1000,\"InputMax\":65535,\"Invert\":true}");
            Assert.False(source.IsConfigured);
            source.AxisName = "ControlMapperPlugin.Brake";
            Assert.True(source.IsConfigured);
            Assert.Equal(0, source.Scale(0));
            Assert.Equal(32768, source.Scale(0.5));
            Assert.Equal(65535, source.Scale(1));
        }
    }
}
