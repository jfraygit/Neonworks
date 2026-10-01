using System;
using Nightshare.Core;
using Nightshare.Core.Protocol;
using Xunit;

namespace Nightshare.Tests
{
    public class ProtocolTests
    {
        [Fact]
        public void HelloRoundTrips()
        {
            var sent = new HelloV1
            {
                ProtocolVersion = ProtocolVersion.Current,
                ModVersion = "0.1.0",
                GameBuildId = "25603526",
                PlayerName = "Rania",
            };

            using var r = new NetReader(sent.Serialise());
            Assert.Equal(MessageType.HelloV1, r.Type);

            var got = HelloV1.Parse(r);
            Assert.Equal(sent.ProtocolVersion, got.ProtocolVersion);
            Assert.Equal(sent.ModVersion, got.ModVersion);
            Assert.Equal(sent.GameBuildId, got.GameBuildId);
            Assert.Equal(sent.PlayerName, got.PlayerName);
        }

        [Fact]
        public void WelcomeRoundTripsThePeerId()
        {
            var id = PeerId.NewId();
            var sent = new WelcomeV1
            {
                AssignedPeerId = id,
                HostPlayerName = "Hana",
                SessionId = "session-abc",
                TotalGameSeconds = 37058,
                GameplayGameDay = 1,
            };

            using var r = new NetReader(sent.Serialise());
            var got = WelcomeV1.Parse(r);

            Assert.Equal(id, got.AssignedPeerId);
            Assert.Equal(37058, got.TotalGameSeconds);
        }

        [Fact]
        public void ClockSyncRoundTrips()
        {
            var sent = new ClockSyncV1
            {
                TotalGameSeconds = 37058,
                GameplayGameDay = 1,
                ClockHour = 10,
                ClockMinute = 17,
                IsPaused = true,
            };

            using var r = new NetReader(sent.Serialise());
            var got = ClockSyncV1.Parse(r);

            Assert.Equal(10, got.ClockHour);
            Assert.Equal(17, got.ClockMinute);
            Assert.True(got.IsPaused);
        }

        /// <summary>
        /// Several of the game's own packet ids are not valid GUIDs. The protocol must
        /// carry them as opaque strings or a join would throw on the host's own data.
        /// </summary>
        [Theory]
        [InlineData("Guid_PLAYER_MANAGER_SAVE")]
        [InlineData("AI-Ai-ai")]
        [InlineData("84F0A877-007I-7O50-B18E-56AF67D8B11B")]
        [InlineData("03H1F65F-148C-9E66-GHQE-4109P1286Q17")]
        public void ManagerPacketCarriesNonGuidPacketIds(string packetGuid)
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            var sent = new ManagerPacketV1
            {
                PacketGuid = packetGuid,
                ManagerTypeName = "Nivalis.TimeOfDayManager",
                Payload = payload,
            };

            using var r = new NetReader(sent.Serialise());
            var got = ManagerPacketV1.Parse(r);

            Assert.Equal(packetGuid, got.PacketGuid);
            Assert.Equal(payload, got.Payload);
        }

        [Fact]
        public void ManagerPacketCarriesASnapshotSizedPayload()
        {
            var payload = new byte[600 * 1024];
            new Random(7).NextBytes(payload);

            var sent = new ManagerPacketV1
            {
                PacketGuid = "1827C75C-5DF6-4ED6-A2A9-86B2A3048071",
                ManagerTypeName = "Nivalis.GhostSystem.Ai.PersonDataManager",
                Payload = payload,
            };

            using var r = new NetReader(sent.Serialise());
            Assert.Equal(payload, ManagerPacketV1.Parse(r).Payload);
        }

        [Fact]
        public void AnUnknownMessageTypeIsFlaggedRatherThanCrashing()
        {
            using var w = new NetWriter((MessageType)60000);
            w.Write(1);

            using var r = new NetReader(w.ToArray());
            Assert.True(r.IsUnknownType);
            Assert.Equal(60000, r.RawType);
        }

        [Fact]
        public void ATruncatedMessageIsRejected()
        {
            var full = new HelloV1
            {
                ProtocolVersion = 1, ModVersion = "0.1.0",
                GameBuildId = "25603526", PlayerName = "Rania",
            }.Serialise();

            using var r = new NetReader(full[..8]);
            Assert.Throws<MalformedMessageException>(() => HelloV1.Parse(r));
        }

        /// <summary>
        /// The failure mode the version discipline exists to prevent: a sender that added
        /// a field and a receiver that did not. Trailing bytes must be an error, not
        /// silently ignored.
        /// </summary>
        [Fact]
        public void ExtraTrailingBytesAreRejectedRatherThanIgnored()
        {
            using var w = new NetWriter(MessageType.ClockSyncV1);
            w.Write(37058).Write(1).Write(10).Write(17).Write(true)
             .Write(999);   // a field a newer sender added

            using var r = new NetReader(w.ToArray());
            var ex = Assert.Throws<MalformedMessageException>(() => ClockSyncV1.Parse(r));
            Assert.Contains("disagree about its layout", ex.Message);
        }

        [Fact]
        public void AMessageShorterThanItsHeaderIsRejected()
        {
            Assert.Throws<MalformedMessageException>(() => new NetReader(new byte[] { 1 }));
            Assert.Throws<MalformedMessageException>(() => new NetReader(Array.Empty<byte>()));
            Assert.Throws<MalformedMessageException>(() => new NetReader(null));
        }

        [Fact]
        public void NullStringsRoundTripAsEmpty()
        {
            var sent = new HelloV1
            {
                ProtocolVersion = 1, ModVersion = null,
                GameBuildId = null, PlayerName = null,
            };

            using var r = new NetReader(sent.Serialise());
            var got = HelloV1.Parse(r);

            Assert.Equal(string.Empty, got.ModVersion);
            Assert.Equal(string.Empty, got.PlayerName);
        }
    }

    public class CompatibilityTests
    {
        private const string Mod = "0.1.0";
        private const string Build = "25603526";

        [Fact]
        public void MatchingPeersAreCompatible()
        {
            Assert.Null(ProtocolVersion.CheckCompatibility(
                ProtocolVersion.Current, Mod, Build, Mod, Build));
        }

        [Fact]
        public void ANewerProtocolIsRefusedAndSaysToUpdate()
        {
            var reason = ProtocolVersion.CheckCompatibility(
                ProtocolVersion.Current + 1, Mod, Build, Mod, Build);

            Assert.NotNull(reason);
            Assert.Contains("Update Nightshare", reason);
        }

        [Fact]
        public void AnOlderProtocolIsRefused()
        {
            var reason = ProtocolVersion.CheckCompatibility(
                ProtocolVersion.MinimumSupported - 1, Mod, Build, Mod, Build);

            Assert.NotNull(reason);
            Assert.Contains("too old", reason);
        }

        [Fact]
        public void AModVersionMismatchIsRefused()
        {
            var reason = ProtocolVersion.CheckCompatibility(
                ProtocolVersion.Current, "0.2.0", Build, Mod, Build);

            Assert.NotNull(reason);
            Assert.Contains("Nightshare versions differ", reason);
        }

        /// <summary>
        /// The one most likely to bite in practice: the game patches, one player updates
        /// and the other has not yet. IL2CPP offsets and save packet layouts both move.
        /// </summary>
        [Fact]
        public void AGameBuildMismatchIsRefused()
        {
            var reason = ProtocolVersion.CheckCompatibility(
                ProtocolVersion.Current, Mod, "99999999", Mod, Build);

            Assert.NotNull(reason);
            Assert.Contains("Nivalis Nights versions differ", reason);
        }
    }
}
