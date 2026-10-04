using Fanadapter.Core;
using Newtonsoft.Json;
using Xunit;

namespace Fanadapter.Core.Tests
{
    /// <summary>
    /// Maps a host property onto the firmware's axis range. Getting this wrong
    /// is how a brake pedal ends up inverted or permanently half-pressed, and
    /// neither shows up until someone is driving.
    /// </summary>
    public class AxisSourceTests
    {
        private static AxisSource Source(double min = 0, double max = 100, bool invert = false) =>
            new AxisSource { PropertyName = "Test.Axis", InputMin = min, InputMax = max, Invert = invert };

        [Theory]
        [InlineData(0, 0)]
        [InlineData(50, 32768)]
        [InlineData(100, 65535)]
        public void MapsTheConfiguredRangeOntoFullTravel(double raw, int expected)
        {
            Assert.Equal(expected, Source().Scale(raw));
        }

        [Fact]
        public void ClampsOutsideTheConfiguredRange()
        {
            var s = Source();
            Assert.Equal(0, s.Scale(-20.0));
            Assert.Equal(65535, s.Scale(180.0));
        }

        [Fact]
        public void InvertFlipsTheTravel()
        {
            var s = Source(invert: true);
            Assert.Equal(65535, s.Scale(0.0));
            Assert.Equal(0, s.Scale(100.0));
            Assert.Equal(32768, s.Scale(50.0));
        }

        [Fact]
        public void SupportsARangeThatIsNotZeroBased()
        {
            var s = Source(min: -1.0, max: 1.0);
            Assert.Equal(0, s.Scale(-1.0));
            Assert.Equal(32768, s.Scale(0.0));
            Assert.Equal(65535, s.Scale(1.0));
        }

        [Fact]
        public void DegenerateRangeDoesNotDivideByZero()
        {
            var s = Source(min: 5, max: 5);
            Assert.Equal(0, s.Scale(5.0));
        }

        [Fact]
        public void BooleanSourcesGiveFullOrNoTravel()
        {
            var s = Source();
            Assert.Equal(65535, s.Scale(true));
            Assert.Equal(0, s.Scale(false));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not a number")]
        public void UnreadableValuesReturnNullSoTheChannelIsLeftAlone(object raw)
        {
            // Returning 0 here would slam the pedal shut on a typo'd property
            // name; null means "no opinion" and the channel keeps its value.
            Assert.Null(Source().Scale(raw));
        }

        [Fact]
        public void NumericStringsAreStillAccepted()
        {
            Assert.Equal(32768, Source().Scale("50"));
        }

        [Fact]
        public void IsConfiguredIgnoresWhitespaceOnlyNames()
        {
            Assert.False(new AxisSource().IsConfigured);
            Assert.False(new AxisSource { PropertyName = "   " }.IsConfigured);
            Assert.True(new AxisSource { PropertyName = "Game.Throttle" }.IsConfigured);
        }

        [Fact]
        public void ChannelLookupCoversEveryAxisTheFirmwareAccepts()
        {
            var drive = new DriveSettings();
            foreach (var channel in Schema.AxisChannelKeys)
            {
                Assert.NotNull(drive.For(channel));
            }
        }

        [Fact]
        public void HasConfiguredAxisIsFalseUntilSomePedalHasASource()
        {
            var drive = new DriveSettings();
            Assert.False(drive.HasConfiguredAxis());

            // Any one channel is enough — a clutch-only setup is legitimate.
            drive.Clutch.PropertyName = "InputStatus.Pedals_Rz";
            Assert.True(drive.HasConfiguredAxis());
        }

        [Fact]
        public void HasConfiguredAxisSurvivesAChannelDeserialisedAsNull()
        {
            // Settings come back from SimHub's JSON store, which can hand back an
            // explicit null for a channel. Resuming the pedal stream asks this
            // question on every connect, so it must not be the thing that throws.
            var drive = new DriveSettings { Throttle = null, Brake = null, Clutch = null, Handbrake = null };
            Assert.False(drive.HasConfiguredAxis());

            drive.Brake = new AxisSource { PropertyName = "InputStatus.Pedals_Y" };
            Assert.True(drive.HasConfiguredAxis());
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(0.5, 32768)]
        [InlineData(1, 65535)]
        public void NativeAxisUsesProcessedUnitRangeWithoutPropertyCalibration(double value, int expected)
        {
            var source = new AxisSource
            {
                UseSimHubAxis = true, AxisName = "ControlMapperPlugin.Brake",
                InputMin = 1000, InputMax = 65000, Invert = true,
            };
            Assert.Equal(expected, source.Scale(value));
        }

        [Fact]
        public void SourceChoiceIsIndependentForEachPedalAndKeepsInactiveSettings()
        {
            var drive = new DriveSettings();
            drive.Throttle.PropertyName = "Test.Throttle";
            drive.Brake = new AxisSource
            {
                UseSimHubAxis = true, AxisName = "ControlMapperPlugin.Brake", AxisMovement = "MaxToMin",
                PropertyName = "Old.Brake", InputMin = 1000, InputMax = 65000, Invert = true,
            };
            var restored = JsonConvert.DeserializeObject<DriveSettings>(JsonConvert.SerializeObject(drive));
            Assert.False(restored.Throttle.UseSimHubAxis);
            Assert.True(restored.Brake.UseSimHubAxis);
            Assert.Equal("ControlMapperPlugin.Brake", restored.Brake.AxisName);
            Assert.Equal("MaxToMin", restored.Brake.AxisMovement);
            restored.Brake.UseSimHubAxis = false;
            Assert.Equal(65535, restored.Brake.Scale(1000));
            Assert.Equal("Old.Brake", restored.Brake.PropertyName);
        }

        [Fact]
        public void LegacySettingsKeepPropertyModeAndNativeModeRequiresItsOwnAssignment()
        {
            var source = JsonConvert.DeserializeObject<AxisSource>(
                "{\"PropertyName\":\"Old.Brake\",\"InputMin\":0,\"InputMax\":65535,\"Invert\":true}");
            Assert.False(source.UseSimHubAxis);
            Assert.Equal(65535, source.Scale(0));
            source.UseSimHubAxis = true;
            Assert.False(source.IsConfigured);
            source.AxisName = "ControlMapperPlugin.Brake";
            Assert.True(source.IsConfigured);
        }
    }
}
