using Nightshare.Core;
using Nightshare.Core.Protocol;
using Xunit;

namespace Nightshare.Tests
{
    public class PlayerMessageTests
    {
        [Fact]
        public void TransformRoundTrips()
        {
            var peer = PeerId.NewId();
            var sent = new PlayerTransformV1
            {
                Peer = peer,
                X = 123.5f, Y = -4.25f, Z = 9001.75f,
                Yaw = 271.5f,
                IsMoving = true,
            };

            using var r = new NetReader(sent.Serialise());
            Assert.Equal(MessageType.PlayerTransformV1, r.Type);

            var got = PlayerTransformV1.Parse(r);
            Assert.Equal(peer, got.Peer);
            Assert.Equal(123.5f, got.X);
            Assert.Equal(-4.25f, got.Y);
            Assert.Equal(9001.75f, got.Z);
            Assert.Equal(271.5f, got.Yaw);
            Assert.True(got.IsMoving);
        }

        [Fact]
        public void TransformSurvivesNegativeAndZeroCoordinates()
        {
            var sent = new PlayerTransformV1
            {
                Peer = PeerId.Host,
                X = -0f, Y = 0f, Z = -1234.5f,
                Yaw = 0f,
                IsMoving = false,
            };

            using var r = new NetReader(sent.Serialise());
            var got = PlayerTransformV1.Parse(r);

            Assert.Equal(0f, got.Y);
            Assert.Equal(-1234.5f, got.Z);
            Assert.False(got.IsMoving);
        }

        /// <summary>
        /// A transform is 35 bytes, so 15 a second is about 500 bytes per player per
        /// second. Pinned because a careless field addition here multiplies by the send
        /// rate and by the player count.
        /// </summary>
        [Fact]
        public void TransformStaysSmall()
        {
            var size = new PlayerTransformV1 { Peer = PeerId.NewId() }.Serialise().Length;
            Assert.True(size <= 40, $"a transform grew to {size} bytes, which is sent 15 times a second");
        }

        [Fact]
        public void PresenceRoundTrips()
        {
            var peer = PeerId.NewId();
            var sent = new PlayerPresenceV1 { Peer = peer, PlayerName = "Rania", IsPresent = true };

            using var r = new NetReader(sent.Serialise());
            var got = PlayerPresenceV1.Parse(r);

            Assert.Equal(peer, got.Peer);
            Assert.Equal("Rania", got.PlayerName);
            Assert.True(got.IsPresent);
        }

        [Fact]
        public void PresenceCarriesDeparture()
        {
            var sent = new PlayerPresenceV1 { Peer = PeerId.NewId(), PlayerName = "Bo", IsPresent = false };

            using var r = new NetReader(sent.Serialise());
            Assert.False(PlayerPresenceV1.Parse(r).IsPresent);
        }
    }
}
