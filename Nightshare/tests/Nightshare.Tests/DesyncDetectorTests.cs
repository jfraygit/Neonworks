using System.Linq;
using System.Text;
using Nightshare.Core;
using Nightshare.Core.Diagnostics;
using Xunit;

namespace Nightshare.Tests
{
    public class StateHashTests
    {
        /// <summary>
        /// Standard FNV-1a 64 vectors. These pin the algorithm itself: changing it would
        /// silently make every peer's hashes incomparable with an older build's, and the
        /// symptom would be a desync report that is itself the bug.
        /// </summary>
        [Theory]
        [InlineData("", 0xcbf29ce484222325UL)]
        [InlineData("a", 0xaf63dc4c8601ec8cUL)]
        [InlineData("foobar", 0x85944171f73967e8UL)]
        public void MatchesKnownFnv1aVectors(string input, ulong expected)
        {
            Assert.Equal(expected, StateHash.Compute(Encoding.ASCII.GetBytes(input)));
        }

        [Fact]
        public void IsStableAcrossCalls()
        {
            var data = Encoding.ASCII.GetBytes("Nivalis.TimeOfDayManager");
            Assert.Equal(StateHash.Compute(data), StateHash.Compute(data));
        }

        [Fact]
        public void DiffersOnASingleChangedByte()
        {
            var a = new byte[] { 1, 2, 3, 4, 5 };
            var b = new byte[] { 1, 2, 3, 4, 6 };
            Assert.NotEqual(StateHash.Compute(a), StateHash.Compute(b));
        }

        [Fact]
        public void RespectsAnOffsetAndCount()
        {
            var whole = new byte[] { 9, 9, 1, 2, 3, 9 };
            var part = new byte[] { 1, 2, 3 };
            Assert.Equal(StateHash.Compute(part), StateHash.Compute(whole, 2, 3));
        }

        [Fact]
        public void HandlesNull() => Assert.Equal(0UL, StateHash.Compute(null));
    }

    public class DesyncDetectorTests
    {
        private static readonly PeerId Host = PeerId.Host;
        private static readonly PeerId Client = PeerId.NewId();

        private const string Clock = "Nivalis.TimeOfDayManager";
        private const string Economy = "Nivalis.Economy.EconomyManager";

        private static ManagerFingerprint Print(string manager, ulong hash, int bytes = 41) =>
            new ManagerFingerprint(manager, hash, bytes);

        [Fact]
        public void AgreeingPeersProduceNoDivergence()
        {
            var d = new DesyncDetector();

            for (int tick = 0; tick < 20; tick++)
            {
                d.Report(Host, tick, Print(Clock, 0xAAAA));
                d.Report(Client, tick, Print(Clock, 0xAAAA));
            }

            Assert.False(d.HasDiverged);
            Assert.Equal("No divergence detected.", d.BuildReport());
        }

        /// <summary>
        /// The detector is validated by feeding it a deliberate divergence. A check that
        /// has never fired proves nothing.
        /// </summary>
        [Fact]
        public void ADeliberateDivergenceIsCaughtAndNamed()
        {
            var d = new DesyncDetector();

            d.Report(Host, 10, Print(Clock, 0xAAAA));
            var found = d.Report(Client, 10, Print(Clock, 0xBBBB));

            Assert.True(d.HasDiverged);
            Assert.Single(found);

            var div = found[0];
            Assert.Equal(Clock, div.ManagerTypeName);
            Assert.Equal(10, div.Tick);
            Assert.Equal(0xAAAAUL, div.HashA);
            Assert.Equal(0xBBBBUL, div.HashB);
            Assert.True(div.IsFirstOccurrence);
        }

        [Fact]
        public void OnlyTheDivergingManagerIsReported()
        {
            var d = new DesyncDetector();

            d.Report(Host, 5, Print(Clock, 0x1111));
            d.Report(Client, 5, Print(Clock, 0x1111));       // agrees
            d.Report(Host, 5, Print(Economy, 0x2222));
            d.Report(Client, 5, Print(Economy, 0x9999));     // differs

            Assert.Single(d.DivergedManagers);
            Assert.Contains(Economy, d.DivergedManagers);
            Assert.DoesNotContain(Clock, d.DivergedManagers);
        }

        /// <summary>
        /// A diverged manager stays diverged and reports every tick. Only the first is
        /// actionable, so the rest must be marked as aftershocks.
        /// </summary>
        [Fact]
        public void OnlyTheFirstOccurrencePerManagerIsFlagged()
        {
            var d = new DesyncDetector();

            for (int tick = 0; tick < 10; tick++)
            {
                d.Report(Host, tick, Print(Economy, 0x1111));
                d.Report(Client, tick, Print(Economy, 0x2222));
            }

            Assert.Equal(10, d.Divergences.Count);
            Assert.Single(d.Divergences, x => x.IsFirstOccurrence);
            Assert.Equal(0, d.Divergences.First(x => x.IsFirstOccurrence).Tick);
        }

        [Fact]
        public void TheReportLeadsWithTheEarliestDivergence()
        {
            var d = new DesyncDetector();

            // Economy goes wrong first, at tick 3. The clock follows at tick 7.
            for (int tick = 0; tick < 12; tick++)
            {
                d.Report(Host, tick, Print(Economy, 0x1111));
                d.Report(Client, tick, Print(Economy, tick >= 3 ? 0x9999UL : 0x1111UL));

                d.Report(Host, tick, Print(Clock, 0x3333));
                d.Report(Client, tick, Print(Clock, tick >= 7 ? 0x8888UL : 0x3333UL));
            }

            var report = d.BuildReport();

            Assert.Contains("2 manager(s) diverged", report);

            var economyAt = report.IndexOf(Economy);
            var clockAt = report.IndexOf(Clock);
            Assert.True(economyAt >= 0 && clockAt >= 0);
            Assert.True(economyAt < clockAt, "the earlier divergence must be listed first");
            Assert.Contains("knock-on effects", report);
        }

        [Fact]
        public void ASizeDifferenceIsSurfacedInTheReport()
        {
            var d = new DesyncDetector();

            d.Report(Host, 1, Print(Economy, 0x1111, bytes: 1279));
            var found = d.Report(Client, 1, Print(Economy, 0x2222, bytes: 900));

            Assert.Contains("size 1279 vs 900", found[0].ToString());
        }

        [Fact]
        public void ASinglePeerAloneNeverDiverges()
        {
            var d = new DesyncDetector();

            for (int tick = 0; tick < 50; tick++)
                d.Report(Host, tick, Print(Clock, (ulong)tick));

            Assert.False(d.HasDiverged);
        }

        [Fact]
        public void TheEventFiresForEachDivergence()
        {
            var d = new DesyncDetector();
            var seen = 0;
            d.Diverged += _ => seen++;

            d.Report(Host, 1, Print(Clock, 0x1));
            d.Report(Client, 1, Print(Clock, 0x2));
            d.Report(Host, 2, Print(Clock, 0x1));
            d.Report(Client, 2, Print(Clock, 0x2));

            Assert.Equal(2, seen);
        }

        /// <summary>
        /// Pruning must not discard a tick a slower peer has yet to report on, or the
        /// detector would invent divergences out of ordinary jitter.
        /// </summary>
        [Fact]
        public void PruningKeepsRecentTicksComparable()
        {
            var d = new DesyncDetector { TickWindow = 8 };

            for (int tick = 0; tick < 200; tick++)
            {
                d.Report(Host, tick, Print(Clock, 0xAAAA));
                d.Report(Client, tick, Print(Clock, 0xAAAA));    // same tick, still compared
            }

            Assert.False(d.HasDiverged);

            // A fresh divergence at the end is still caught after all that pruning.
            d.Report(Host, 200, Print(Clock, 0xAAAA));
            d.Report(Client, 200, Print(Clock, 0xFFFF));
            Assert.True(d.HasDiverged);
        }

        [Fact]
        public void ResetClearsEverything()
        {
            var d = new DesyncDetector();
            d.Report(Host, 1, Print(Clock, 0x1));
            d.Report(Client, 1, Print(Clock, 0x2));
            Assert.True(d.HasDiverged);

            d.Reset();

            Assert.False(d.HasDiverged);
            Assert.Empty(d.Divergences);
            Assert.Empty(d.DivergedManagers);
        }
    }
}
