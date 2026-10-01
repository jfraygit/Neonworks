using System;
using System.IO;
using Nightshare.Core;
using Nightshare.Core.Diagnostics;
using Nightshare.Core.Protocol;
using Xunit;

namespace Nightshare.Tests
{
    public class PacketRecorderTests
    {
        private static MemoryStream RecordTo(Action<PacketRecorder> record,
                                             string mod = "0.1.0", string build = "25603526", bool isHost = true)
        {
            var ms = new NonClosingMemoryStream();
            using (var rec = new PacketRecorder(ms, mod, build, isHost))
                record(rec);

            ms.Position = 0;
            return ms;
        }

        [Fact]
        public void RoundTripsTheHeader()
        {
            var ms = RecordTo(_ => { }, mod: "0.4.2", build: "12345678", isHost: false);
            var (info, packets) = PacketReplay.Read(ms);

            Assert.Equal(1, info.FormatVersion);
            Assert.Equal("0.4.2", info.ModVersion);
            Assert.Equal("12345678", info.GameBuildId);
            Assert.False(info.IsHost);
            Assert.Empty(packets);
        }

        [Fact]
        public void RoundTripsPacketsInOrder()
        {
            var peer = PeerId.NewId();

            var ms = RecordTo(rec =>
            {
                rec.Record(0, PacketDirection.Inbound, peer, new byte[] { 1, 0, 0xAA });
                rec.Record(120, PacketDirection.Outbound, peer, new byte[] { 2, 0, 0xBB });
                rec.Record(999, PacketDirection.Inbound, peer, new byte[] { 40, 0, 0xCC });
            });

            var (_, packets) = PacketReplay.Read(ms);

            Assert.Equal(3, packets.Count);
            Assert.Equal(0, packets[0].ElapsedMs);
            Assert.Equal(120, packets[1].ElapsedMs);
            Assert.Equal(999, packets[2].ElapsedMs);
            Assert.Equal(PacketDirection.Outbound, packets[1].Direction);
            Assert.Equal(peer, packets[0].Peer);
        }

        /// <summary>
        /// The trace must be readable as messages, not just bytes, or summarising it
        /// requires the build that produced it.
        /// </summary>
        [Fact]
        public void DecodesTheMessageTypeFromThePayload()
        {
            var payload = new ClockSyncV1
            {
                TotalGameSeconds = 37058, GameplayGameDay = 1,
                ClockHour = 10, ClockMinute = 17, IsPaused = false,
            }.Serialise();

            var ms = RecordTo(rec => rec.Record(5, PacketDirection.Inbound, PeerId.Host, payload));
            var (_, packets) = PacketReplay.Read(ms);

            Assert.Equal((ushort)MessageType.ClockSyncV1, packets[0].MessageType);
        }

        [Fact]
        public void CarriesASnapshotSizedPayload()
        {
            var big = new byte[600 * 1024];
            new Random(3).NextBytes(big);

            var ms = RecordTo(rec => rec.Record(1, PacketDirection.Inbound, PeerId.Host, big));
            var (_, packets) = PacketReplay.Read(ms);

            Assert.Equal(big, packets[0].Payload);
        }

        [Fact]
        public void HandlesEmptyAndNullPayloads()
        {
            var ms = RecordTo(rec =>
            {
                rec.Record(1, PacketDirection.Inbound, PeerId.Host, Array.Empty<byte>());
                rec.Record(2, PacketDirection.Inbound, PeerId.Host, null);
            });

            var (_, packets) = PacketReplay.Read(ms);

            Assert.Equal(2, packets.Count);
            Assert.Empty(packets[0].Payload);
            Assert.Empty(packets[1].Payload);
        }

        [Fact]
        public void CountsWhatItRecorded()
        {
            var ms = new NonClosingMemoryStream();
            using var rec = new PacketRecorder(ms, "0.1.0", "25603526", true);

            Assert.Equal(0, rec.PacketCount);
            rec.Record(1, PacketDirection.Inbound, PeerId.Host, new byte[] { 1, 0 });
            rec.Record(2, PacketDirection.Inbound, PeerId.Host, new byte[] { 1, 0 });
            Assert.Equal(2, rec.PacketCount);
        }

        [Fact]
        public void RecordingAfterDisposeIsIgnoredRatherThanThrowing()
        {
            var ms = new NonClosingMemoryStream();
            var rec = new PacketRecorder(ms, "0.1.0", "25603526", true);
            rec.Dispose();

            // A diagnostic must never be the thing that crashes a session on shutdown.
            rec.Record(1, PacketDirection.Inbound, PeerId.Host, new byte[] { 1, 0 });
            Assert.Equal(0, rec.PacketCount);
        }

        [Fact]
        public void DisposingTwiceIsSafe()
        {
            var rec = new PacketRecorder(new NonClosingMemoryStream(), "0.1.0", "25603526", true);
            rec.Dispose();
            rec.Dispose();
        }

        [Fact]
        public void RejectsAFileThatIsNotATrace()
        {
            using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Throws<InvalidDataException>(() => PacketReplay.Read(ms));
        }

        [Fact]
        public void RejectsATruncatedTrace()
        {
            var ms = RecordTo(rec =>
                rec.Record(1, PacketDirection.Inbound, PeerId.Host, new byte[500]));

            var bytes = ms.ToArray();
            using var truncated = new MemoryStream(bytes[..(bytes.Length - 200)]);

            Assert.Throws<InvalidDataException>(() => PacketReplay.Read(truncated));
        }

        [Fact]
        public void SummaryGroupsByMessageTypeAndDirection()
        {
            var clock = new ClockSyncV1().Serialise();
            var hello = new HelloV1().Serialise();

            var ms = RecordTo(rec =>
            {
                rec.Record(0, PacketDirection.Inbound, PeerId.Host, clock);
                rec.Record(10, PacketDirection.Inbound, PeerId.Host, clock);
                rec.Record(20, PacketDirection.Outbound, PeerId.Host, hello);
            });

            var (info, packets) = PacketReplay.Read(ms);
            var summary = PacketReplay.Summarise(info, packets);

            Assert.Contains("packets     : 3", summary);
            Assert.Contains("span        : 20 ms", summary);
            Assert.Contains($"type {(ushort)MessageType.ClockSyncV1,-6} x2", summary);
            Assert.Contains($"type {(ushort)MessageType.HelloV1,-6} x1", summary);
        }

        /// <summary>MemoryStream that survives the recorder disposing it, so tests can read it back.</summary>
        private sealed class NonClosingMemoryStream : MemoryStream
        {
            protected override void Dispose(bool disposing) { /* keep the buffer readable */ }
        }
    }
}
