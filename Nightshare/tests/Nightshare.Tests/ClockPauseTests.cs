using Nightshare.Core.Protocol;
using Xunit;

namespace Nightshare.Tests
{
    /// <summary>
    /// The pause flag on the clock message.
    /// <para>
    /// The guest-side behaviour it drives (taking the game's own pause lock) needs a live
    /// IL2CPP domain and cannot be tested here. What can be pinned is the part that was
    /// actually wrong: the flag has to survive the wire, and it has to be reported every
    /// broadcast rather than only when it changes, because a paused host's clock stops
    /// moving and the old code took "time unchanged" as "nothing to say".
    /// </para>
    /// </summary>
    public class ClockPauseTests
    {
        private static ClockSyncV1 RoundTrip(ClockSyncV1 sent)
        {
            using var r = new NetReader(sent.Serialise());
            Assert.Equal(MessageType.ClockSyncV1, r.Type);
            return ClockSyncV1.Parse(r);
        }

        [Fact]
        public void PausedSurvivesTheWire()
        {
            var got = RoundTrip(new ClockSyncV1
            {
                TotalGameSeconds = 119842,
                GameplayGameDay = 2,
                ClockHour = 9,
                ClockMinute = 18,
                IsPaused = true,
            });

            Assert.True(got.IsPaused);
            Assert.Equal(119842, got.TotalGameSeconds);
            Assert.Equal(2, got.GameplayGameDay);
            Assert.Equal(9, got.ClockHour);
            Assert.Equal(18, got.ClockMinute);
        }

        [Fact]
        public void RunningSurvivesTheWire()
        {
            Assert.False(RoundTrip(new ClockSyncV1 { IsPaused = false }).IsPaused);
        }

        /// <summary>
        /// Two broadcasts with identical time but different pause state must be
        /// distinguishable. This is the shape of the original bug: a paused host's seconds
        /// stop changing, so anything treating the time as the whole message loses the pause.
        /// </summary>
        [Fact]
        public void PauseIsVisibleEvenWhenTheClockHasNotMoved()
        {
            const int frozen = 50_000;

            var running = RoundTrip(new ClockSyncV1 { TotalGameSeconds = frozen, IsPaused = false });
            var paused = RoundTrip(new ClockSyncV1 { TotalGameSeconds = frozen, IsPaused = true });

            Assert.Equal(running.TotalGameSeconds, paused.TotalGameSeconds);
            Assert.NotEqual(running.IsPaused, paused.IsPaused);
        }

        /// <summary>
        /// A day advance is the one time the raw seconds legitimately go down, so it must
        /// not be mistaken for the host having reloaded.
        /// </summary>
        [Fact]
        public void WrappingPastMidnightIsADayAdvanceNotAReload()
        {
            var beforeMidnight = RoundTrip(new ClockSyncV1
            {
                TotalGameSeconds = 86_300,
                GameplayGameDay = 2,
            });

            var afterMidnight = RoundTrip(new ClockSyncV1
            {
                TotalGameSeconds = 100,
                GameplayGameDay = 3,
            });

            Assert.True(afterMidnight.TotalGameSeconds < beforeMidnight.TotalGameSeconds);
            Assert.True(afterMidnight.GameplayGameDay > beforeMidnight.GameplayGameDay);
        }
    }
}
