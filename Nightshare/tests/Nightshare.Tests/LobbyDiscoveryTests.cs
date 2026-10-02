using System.Collections.Generic;
using System.Net;
using Nightshare.Core.Discovery;
using Nightshare.Core.Protocol;
using Xunit;

namespace Nightshare.Tests
{
    /// <summary>
    /// Finding a session on the local network, so nobody has to exchange an IP address.
    /// </summary>
    public class LobbyDiscoveryTests
    {
        private static LobbyBeaconV1 RoundTrip(LobbyBeaconV1 sent)
        {
            using var r = new NetReader(sent.Serialise());
            Assert.Equal(MessageType.LobbyBeaconV1, r.Type);
            return LobbyBeaconV1.Parse(r);
        }

        [Fact]
        public void ABeaconSurvivesTheWire()
        {
            var got = RoundTrip(new LobbyBeaconV1
            {
                HostName = "Rania",
                Port = 7777,
                ProtocolVersion = 3,
                ModVersion = "0.1.0",
                GameBuildId = "72380928-12659680",
                PlayerCount = 1,
                SessionId = "f1037ca8fafe40fba8db16cac8634cc5",
            });

            Assert.Equal("Rania", got.HostName);
            Assert.Equal(7777, got.Port);
            Assert.Equal(3, got.ProtocolVersion);
            Assert.Equal("0.1.0", got.ModVersion);
            Assert.Equal("72380928-12659680", got.GameBuildId);
            Assert.Equal(1, got.PlayerCount);
            Assert.Equal("f1037ca8fafe40fba8db16cac8634cc5", got.SessionId);
        }

        /// <summary>
        /// A beacon is read off the open network, where anything can send anything. Nothing
        /// in it may be trusted enough to crash on.
        /// </summary>
        [Fact]
        public void EmptyFieldsAreNotACrash()
        {
            var got = RoundTrip(new LobbyBeaconV1());

            Assert.Equal("", got.HostName);
            Assert.Equal("", got.SessionId);
            Assert.Equal(0, got.Port);
        }

        // ------------------------------------------------------------ the browser

        /// <summary>
        /// The browser's parsing and ageing, exercised without a socket by feeding it the
        /// bytes a host would have broadcast.
        /// </summary>
        private static LobbyBrowser BrowserExpecting(int protocol, string mod, string build)
        {
            return new LobbyBrowser
            {
                ExpectedProtocol = protocol,
                ExpectedModVersion = mod,
                ExpectedGameBuild = build,
            };
        }

        private static LobbyBeaconV1 Beacon(string id, string name = "Host",
                                            int protocol = 3, string mod = "0.1.0",
                                            string build = "A-B")
        {
            return new LobbyBeaconV1
            {
                SessionId = id,
                HostName = name,
                Port = 7777,
                ProtocolVersion = protocol,
                ModVersion = mod,
                GameBuildId = build,
                PlayerCount = 0,
            };
        }

        [Fact]
        public void ASessionIsListedUnderTheAddressItCameFrom()
        {
            var browser = BrowserExpecting(3, "0.1.0", "A-B");
            var found = new List<FoundLobby>();

            browser.Accept(Beacon("s1", "Rania").Serialise(),
                                  new IPEndPoint(IPAddress.Parse("192.168.1.42"), 51000));
            browser.Snapshot(found);

            var lobby = Assert.Single(found);
            Assert.Equal("Rania", lobby.HostName);

            // The sender's address, not anything the host claimed about itself.
            Assert.Equal("192.168.1.42", lobby.Address);
            Assert.Equal("192.168.1.42:7777", lobby.Endpoint);
            Assert.True(lobby.Compatible);
        }

        [Theory]
        [InlineData(4, "0.1.0", "A-B", "Different Nightshare protocol")]
        [InlineData(3, "0.2.0", "A-B", "They are on Nightshare 0.2.0")]
        [InlineData(3, "0.1.0", "C-D", "Different game version")]
        public void AMismatchIsListedWithAReasonRatherThanHidden(
            int protocol, string mod, string build, string reason)
        {
            var browser = BrowserExpecting(3, "0.1.0", "A-B");
            var found = new List<FoundLobby>();

            browser.Accept(Beacon("s1", "Rania", protocol, mod, build).Serialise(),
                                  new IPEndPoint(IPAddress.Loopback, 51000));
            browser.Snapshot(found);

            var lobby = Assert.Single(found);
            Assert.False(lobby.Compatible);
            Assert.Equal(reason, lobby.Incompatibility);
        }

        /// <summary>
        /// A host that crashes never says goodbye, so the list has to forget on its own.
        /// </summary>
        [Fact]
        public void ASessionThatStopsAnnouncingDisappears()
        {
            var browser = BrowserExpecting(3, "0.1.0", "A-B");
            browser.ForgetAfterSeconds = 4f;
            var found = new List<FoundLobby>();

            browser.Accept(Beacon("s1").Serialise(),
                                  new IPEndPoint(IPAddress.Loopback, 51000));

            browser.Tick(3f);
            browser.Snapshot(found);
            Assert.Single(found);

            browser.Tick(2f);          // 5s total, past the limit
            browser.Snapshot(found);
            Assert.Empty(found);
        }

        [Fact]
        public void AFreshBeaconKeepsASessionAlive()
        {
            var browser = BrowserExpecting(3, "0.1.0", "A-B");
            browser.ForgetAfterSeconds = 4f;
            var found = new List<FoundLobby>();

            var sender = new IPEndPoint(IPAddress.Loopback, 51000);

            browser.Accept(Beacon("s1").Serialise(), sender);
            browser.Tick(3f);
            browser.Accept(Beacon("s1").Serialise(), sender);   // heard again
            browser.Tick(3f);

            browser.Snapshot(found);
            Assert.Single(found);
        }

        [Fact]
        public void TwoHostsAreTwoEntries()
        {
            var browser = BrowserExpecting(3, "0.1.0", "A-B");
            var found = new List<FoundLobby>();

            browser.Accept(Beacon("s1", "Rania").Serialise(),
                                  new IPEndPoint(IPAddress.Parse("192.168.1.1"), 51000));
            browser.Accept(Beacon("s2", "Jonas").Serialise(),
                                  new IPEndPoint(IPAddress.Parse("192.168.1.2"), 51000));

            browser.Snapshot(found);
            Assert.Equal(2, found.Count);
        }

        /// <summary>
        /// The discovery port is open to the whole network and most of what arrives on it
        /// will not be ours. None of it may throw.
        /// </summary>
        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 1 })]
        [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
        public void RubbishOnThePortIsIgnored(byte[] data)
        {
            var browser = BrowserExpecting(3, "0.1.0", "A-B");
            var found = new List<FoundLobby>();

            browser.Accept(data, new IPEndPoint(IPAddress.Loopback, 51000));

            browser.Snapshot(found);
            Assert.Empty(found);
        }
    }
}
