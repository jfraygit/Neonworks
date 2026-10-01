using Nightshare.Core.Protocol;
using Xunit;

namespace Nightshare.Tests
{
    public class SleepMessageTests
    {
        [Fact]
        public void SleepRequestRoundTrips()
        {
            var sent = new SleepRequestV1 { WantsToSleep = true, TargetGameSeconds = 21600 };

            using var r = new NetReader(sent.Serialise());
            Assert.Equal(MessageType.SleepRequestV1, r.Type);

            var got = SleepRequestV1.Parse(r);
            Assert.True(got.WantsToSleep);
            Assert.Equal(21600, got.TargetGameSeconds);
        }

        /// <summary>Getting out of bed must be as expressible as getting into it.</summary>
        [Fact]
        public void SleepRequestCarriesACancellation()
        {
            var sent = new SleepRequestV1 { WantsToSleep = false, TargetGameSeconds = 0 };

            using var r = new NetReader(sent.Serialise());
            Assert.False(SleepRequestV1.Parse(r).WantsToSleep);
        }

        [Fact]
        public void SleepStateRoundTrips()
        {
            var sent = new SleepStateV1 { ReadyCount = 1, TotalCount = 2, WaitingOn = "Rania" };

            using var r = new NetReader(sent.Serialise());
            var got = SleepStateV1.Parse(r);

            Assert.Equal(1, got.ReadyCount);
            Assert.Equal(2, got.TotalCount);
            Assert.Equal("Rania", got.WaitingOn);
        }

        [Fact]
        public void SleepStateHandlesSeveralNames()
        {
            var sent = new SleepStateV1 { ReadyCount = 1, TotalCount = 4, WaitingOn = "Rania, Bo, Hana" };

            using var r = new NetReader(sent.Serialise());
            Assert.Equal("Rania, Bo, Hana", SleepStateV1.Parse(r).WaitingOn);
        }

        [Fact]
        public void DayAdvancedRoundTrips()
        {
            var sent = new DayAdvancedV1 { TotalGameSeconds = 21600, GameplayGameDay = 5 };

            using var r = new NetReader(sent.Serialise());
            var got = DayAdvancedV1.Parse(r);

            Assert.Equal(21600, got.TotalGameSeconds);
            Assert.Equal(5, got.GameplayGameDay);
        }

        /// <summary>
        /// Sleeping through midnight moves the day forward while the seconds-in-day count
        /// goes down. Both halves have to survive, or a client wakes on the wrong day.
        /// </summary>
        [Fact]
        public void DayAdvancedSurvivesWrappingPastMidnight()
        {
            var sent = new DayAdvancedV1 { TotalGameSeconds = 21600, GameplayGameDay = 6 };

            using var r = new NetReader(sent.Serialise());
            var got = DayAdvancedV1.Parse(r);

            Assert.True(got.TotalGameSeconds < 79200, "woke earlier in the day than they slept");
            Assert.Equal(6, got.GameplayGameDay);
        }
    }
}
