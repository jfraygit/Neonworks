using System;
using Nightshare.Core.Protocol;
using Nightshare.Core.Transport;
using Xunit;

namespace Nightshare.Tests
{
    public class SaveMessageTests
    {
        [Fact]
        public void SaveRequestRoundTrips()
        {
            var sent = new SaveRequestV1 { PlayerName = "Rania" };

            using var r = new NetReader(sent.Serialise());
            Assert.Equal(MessageType.SaveRequestV1, r.Type);
            Assert.Equal("Rania", SaveRequestV1.Parse(r).PlayerName);
        }

        [Fact]
        public void SaveTransferRoundTripsItsHeader()
        {
            var sent = new SaveTransferV1
            {
                SaveName = "nightshare_visiting",
                GameplayGameDay = 3,
                Data = new byte[] { 1, 2, 3, 4 },
            };

            using var r = new NetReader(sent.Serialise());
            var got = SaveTransferV1.Parse(r);

            Assert.Equal("nightshare_visiting", got.SaveName);
            Assert.Equal(3, got.GameplayGameDay);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, got.Data);
        }

        /// <summary>
        /// A real save measured 6.5 MB. The whole design depends on that surviving the
        /// wire intact, so it is pinned at a realistic size rather than a token one.
        /// </summary>
        [Fact]
        public void SaveTransferCarriesARealSizedSaveFile()
        {
            var save = new byte[6_838_080];
            new Random(99).NextBytes(save);

            var sent = new SaveTransferV1
            {
                SaveName = "nightshare_visiting",
                GameplayGameDay = 12,
                Data = save,
            };

            using var r = new NetReader(sent.Serialise());
            var got = SaveTransferV1.Parse(r);

            Assert.Equal(save.Length, got.Data.Length);
            Assert.Equal(save, got.Data);
        }

        /// <summary>
        /// A save is sent as one message, so it has to fit in one frame. Pinned so a
        /// future change to MaxFrameSize cannot silently break every join, and so that a
        /// save growing past the limit is caught here rather than in play.
        /// </summary>
        [Fact]
        public void ARealSizedSaveFitsInOneFrame()
        {
            var message = new SaveTransferV1
            {
                SaveName = "nightshare_visiting",
                GameplayGameDay = 1,
                Data = new byte[6_838_080],
            }.Serialise();

            Assert.True(message.Length < FrameCodec.MaxFrameSize,
                $"a {message.Length:N0} byte save exceeds the {FrameCodec.MaxFrameSize:N0} byte frame limit; " +
                $"the transfer needs chunking");
        }

        [Fact]
        public void AnEmptySaveIsStillWellFormed()
        {
            var sent = new SaveTransferV1
            {
                SaveName = "nightshare_visiting",
                GameplayGameDay = 0,
                Data = Array.Empty<byte>(),
            };

            using var r = new NetReader(sent.Serialise());
            Assert.Empty(SaveTransferV1.Parse(r).Data);
        }
    }
}
