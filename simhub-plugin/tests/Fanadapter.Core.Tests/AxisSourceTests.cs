using Fanadapter.Core;
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
    }
}
