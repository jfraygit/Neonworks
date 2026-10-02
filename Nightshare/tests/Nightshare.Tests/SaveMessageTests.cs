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
            var sent = new SaveTransferV2
            {
                SaveName = "nightshare_visiting",
                GameplayGameDay = 3,
                Data = new byte[] { 1, 2, 3, 4 },
            };

            using var r = new NetReader(sent.Serialise());
            var got = SaveTransferV2.Parse(r);

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

            var sent = new SaveTransferV2
            {
                SaveName = "nightshare_visiting",
                GameplayGameDay = 12,
                Data = save,
            };

            using var r = new NetReader(sent.Serialise());
            var got = SaveTransferV2.Parse(r);

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
            var message = new SaveTransferV2
            {
                SaveName = "nightshare_visiting",
                GameplayGameDay = 1,
                Data = new byte[6_838_080],
            }.Serialise();

            Assert.True(message.Length < FrameCodec.MaxFrameSize,
                $"a {message.Length:N0} byte save exceeds the {FrameCodec.MaxFrameSize:N0} byte frame limit; " +
                $"the transfer needs chunking");
        }

        /// <summary>
        /// The reason V2 exists. Loading a save from inside a running world does not restore
        /// the player's transform, so the arrival point travels separately and has to come
        /// through the wire exactly.
        /// </summary>
        [Fact]
        public void TheArrivalPointSurvivesTheWire()
        {
            var sent = new SaveTransferV2
            {
                SaveName = "nightshare_visiting",
                GameplayGameDay = 2,
                Data = new byte[] { 7 },
                X = 31.04f,
                Y = -0.72f,
                Z = 24.55f,
                Yaw = 216.5f,
                HasArrivalPoint = true,
            };

            using var r = new NetReader(sent.Serialise());
            var got = SaveTransferV2.Parse(r);

            Assert.True(got.HasArrivalPoint);
            Assert.Equal(31.04f, got.X, 3);
            Assert.Equal(-0.72f, got.Y, 3);
            Assert.Equal(24.55f, got.Z, 3);
            Assert.Equal(216.5f, got.Yaw, 3);
        }

        /// <summary>
        /// A host whose player could not be found sends no arrival point, and the guest must
        /// be able to tell that from an arrival point that happens to be the world origin.
        /// Teleporting someone to (0, 0, 0) is a worse failure than leaving them put.
        /// </summary>
        /// <summary>
        /// An indoor arrival carries the apartment to enter; an outdoor one carries nothing,
        /// and the two must not be confusable. Entering an apartment the host is not in is
        /// worse than entering none.
        /// </summary>
        [Fact]
        public void TheApartmentTravelsWithAnIndoorArrival()
        {
            var indoors = new SaveTransferV2
            {
                SaveName = "nightshare_visiting",
                Data = Array.Empty<byte>(),
                HasArrivalPoint = true,
                ApartmentGuid = "6f1c2a9e4b7d48c0",
            };

            using var r1 = new NetReader(indoors.Serialise());
            Assert.Equal("6f1c2a9e4b7d48c0", SaveTransferV2.Parse(r1).ApartmentGuid);

            var outdoors = new SaveTransferV2
            {
                SaveName = "nightshare_visiting",
                Data = Array.Empty<byte>(),
                HasArrivalPoint = true,
                ApartmentGuid = null,
            };

            using var r2 = new NetReader(outdoors.Serialise());
            Assert.True(string.IsNullOrEmpty(SaveTransferV2.Parse(r2).ApartmentGuid));
        }

        /// <summary>
        /// The clock travels with the save for the same reason the position does: the load
        /// does not restore it. A guest that arrives ahead of the host would otherwise stay
        /// ahead, because ordinary corrections only move forward.
        /// </summary>
        [Fact]
        public void TheHostsClockTravelsWithTheSave()
        {
            var sent = new SaveTransferV2
            {
                SaveName = "nightshare_visiting",
                Data = Array.Empty<byte>(),
                GameplayGameDay = 2,
                TotalGameSeconds = 158_460,
            };

            using var r = new NetReader(sent.Serialise());
            var got = SaveTransferV2.Parse(r);

            Assert.Equal(158_460, got.TotalGameSeconds);
            Assert.Equal(2, got.GameplayGameDay);
        }

        [Fact]
        public void NoArrivalPointIsDistinctFromTheOrigin()
        {
            var sent = new SaveTransferV2
            {
                SaveName = "nightshare_visiting",
                Data = Array.Empty<byte>(),
                X = 0f, Y = 0f, Z = 0f,
                HasArrivalPoint = false,
            };

            using var r = new NetReader(sent.Serialise());
            var got = SaveTransferV2.Parse(r);

            Assert.False(got.HasArrivalPoint);
            Assert.Equal(0f, got.X);
        }

        [Fact]
        public void AnEmptySaveIsStillWellFormed()
        {
            var sent = new SaveTransferV2
            {
                SaveName = "nightshare_visiting",
                GameplayGameDay = 0,
                Data = Array.Empty<byte>(),
            };

            using var r = new NetReader(sent.Serialise());
            Assert.Empty(SaveTransferV2.Parse(r).Data);
        }
    }
}
