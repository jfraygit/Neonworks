using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nightshare.Core;
using Nightshare.Core.Protocol;
using Nightshare.Core.Session;
using Nightshare.Core.Transport;
using Xunit;

namespace Nightshare.Tests
{
    /// <summary>
    /// End to end over real loopback sockets: two sessions, a real handshake, real
    /// rejection paths. This is the code the game and the fake peer both run.
    /// </summary>
    public class SessionTests : IDisposable
    {
        private const string Mod = "0.1.0";
        private const string Build = "25603526";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private readonly List<NightshareSession> _sessions = new();

        public void Dispose()
        {
            foreach (var s in _sessions)
            {
                try { s.Dispose(); } catch { }
            }
        }

        [Fact]
        public void AMatchingClientJoins()
        {
            var (host, client) = Pair();

            PumpUntil(() => client.IsActive && host.Peers.Count == 1, host, client);

            Assert.True(host.IsHost);
            Assert.False(client.IsHost);
            Assert.Equal(SessionState.Active, client.State);
            Assert.Single(host.Peers);
            Assert.Single(client.Peers);
        }

        [Fact]
        public void TheClientAdoptsTheIdTheHostAssigned()
        {
            var (host, client) = Pair();
            PumpUntil(() => client.IsActive && host.Peers.Count == 1, host, client);

            PeerId assigned = default;
            foreach (var p in host.Peers) assigned = p.Id;

            Assert.Equal(assigned, client.LocalPeer);
            Assert.NotEqual(PeerId.Host, client.LocalPeer);
        }

        [Fact]
        public void BothSidesLearnEachOthersNames()
        {
            var (host, client) = Pair(hostName: "Hana", clientName: "Rania");
            PumpUntil(() => client.IsActive && host.Peers.Count == 1, host, client);

            foreach (var p in host.Peers) Assert.Equal("Rania", p.Name);
            foreach (var p in client.Peers)
            {
                Assert.Equal("Hana", p.Name);
                Assert.True(p.IsHost);
            }
        }

        [Fact]
        public void TheClientSharesTheHostsSessionId()
        {
            var (host, client) = Pair();
            PumpUntil(() => client.IsActive, host, client);

            Assert.False(string.IsNullOrEmpty(host.SessionId));
            Assert.Equal(host.SessionId, client.SessionId);
        }

        [Fact]
        public void TheHostReportsTheClockInTheWelcome()
        {
            var port = FreePort();
            var host = NewSession();
            host.GameSecondsProvider = () => 37058;
            host.GameDayProvider = () => 4;
            host.Host(new TcpTransport(), port.ToString(), Identity("Hana"));

            var client = NewSession();
            string joinLog = null;
            client.Log += m => { if (m.Contains("day ")) joinLog = m; };
            client.Join(new TcpTransport(), $"127.0.0.1:{port}", Identity("Rania"));

            PumpUntil(() => client.IsActive, host, client);

            Assert.NotNull(joinLog);
            Assert.Contains("day 4", joinLog);
            Assert.Contains("37058", joinLog);
        }

        // ------------------------------------------------------------ rejection paths

        /// <summary>
        /// The one most likely to happen in practice. The game patches, one player updates
        /// and the other has not. Both must be told clearly rather than desyncing later.
        /// </summary>
        [Fact]
        public void AGameBuildMismatchIsRefusedWithAReadableReason()
        {
            var port = FreePort();
            var host = NewSession();
            host.Host(new TcpTransport(), port.ToString(), Identity("Hana", build: "25603526"));

            var client = NewSession();
            string failure = null;
            client.SessionFailed += r => failure = r;
            client.Join(new TcpTransport(), $"127.0.0.1:{port}", Identity("Rania", build: "99999999"));

            PumpUntil(() => failure != null, host, client);

            Assert.Equal(SessionState.Failed, client.State);
            Assert.Contains("Nivalis Nights versions differ", failure);
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void AModVersionMismatchIsRefused()
        {
            var port = FreePort();
            var host = NewSession();
            host.Host(new TcpTransport(), port.ToString(), Identity("Hana", mod: "0.1.0"));

            var client = NewSession();
            string failure = null;
            client.SessionFailed += r => failure = r;
            client.Join(new TcpTransport(), $"127.0.0.1:{port}", Identity("Rania", mod: "0.9.9"));

            PumpUntil(() => failure != null, host, client);

            Assert.Contains("Nightshare versions differ", failure);
            Assert.Empty(host.Peers);
        }

        /// <summary>
        /// A refused client must receive the explanation before the socket closes.
        /// Closing outright would show the player a bare disconnect and no reason.
        /// </summary>
        [Fact]
        public void ARefusedClientGetsTheReasonBeforeTheSocketCloses()
        {
            var port = FreePort();
            var host = NewSession();
            host.Host(new TcpTransport(), port.ToString(), Identity("Hana", build: "25603526"));

            var client = NewSession();
            string failure = null;
            client.SessionFailed += r => failure ??= r;
            client.Join(new TcpTransport(), $"127.0.0.1:{port}", Identity("Rania", build: "11111111"));

            PumpUntil(() => failure != null, host, client);

            // The reason is the compatibility message, not "disconnected".
            Assert.Contains("versions differ", failure);
            Assert.DoesNotContain("Disconnected from host", failure);
        }

        // ------------------------------------------------------------ lifetime

        [Fact]
        public void TheHostSurvivesAClientLeaving()
        {
            var (host, client) = Pair();
            PumpUntil(() => client.IsActive && host.Peers.Count == 1, host, client);

            RemotePeer left = null;
            host.PeerLeft += (p, _) => left = p;

            client.Leave();
            PumpUntil(() => left != null, host);

            Assert.NotNull(left);
            Assert.Empty(host.Peers);
            Assert.Equal(SessionState.Active, host.State);   // still hosting, just alone
        }

        [Fact]
        public void AClientFailsWhenTheHostGoesAway()
        {
            var (host, client) = Pair();
            PumpUntil(() => client.IsActive, host, client);

            string failure = null;
            client.SessionFailed += r => failure = r;

            host.Leave();
            PumpUntil(() => failure != null, client);

            Assert.Equal(SessionState.Failed, client.State);
            Assert.Contains("Disconnected from host", failure);
        }

        // ------------------------------------------------------------ robustness

        /// <summary>
        /// Forward compatibility. A newer peer may send types this build has no handler
        /// for; ignoring them must not drop the connection.
        /// </summary>
        [Fact]
        public void AnUnknownMessageTypeIsIgnoredWithoutDroppingThePeer()
        {
            var (host, client) = Pair();
            PumpUntil(() => client.IsActive && host.Peers.Count == 1, host, client);

            using var w = new NetWriter((MessageType)60000);
            w.Write(12345);
            client.SendToHost(w.ToArray());

            PumpFor(TimeSpan.FromMilliseconds(400), host, client);

            Assert.Single(host.Peers);
            Assert.Equal(SessionState.Active, client.State);
        }

        [Fact]
        public void AnApplicationMessageReachesTheOtherSide()
        {
            var (host, client) = Pair();
            PumpUntil(() => client.IsActive && host.Peers.Count == 1, host, client);

            ClockSyncV1 received = null;
            client.MessageReceived += (_, r) =>
            {
                if (r.Type == MessageType.ClockSyncV1) received = ClockSyncV1.Parse(r);
            };

            host.Broadcast(new ClockSyncV1
            {
                TotalGameSeconds = 37058, GameplayGameDay = 1,
                ClockHour = 10, ClockMinute = 17, IsPaused = false,
            }.Serialise());

            PumpUntil(() => received != null, host, client);

            Assert.Equal(10, received.ClockHour);
            Assert.Equal(17, received.ClockMinute);
        }

        [Fact]
        public void GarbageOnTheWireDropsThePeerRatherThanCrashingTheHost()
        {
            var port = FreePort();
            var host = NewSession();
            host.Host(new TcpTransport(), port.ToString(), Identity("Hana"));

            // A raw socket that is not Nightshare at all.
            using var rogue = new TcpClient();
            rogue.Connect(IPAddress.Loopback, port);
            var stream = rogue.GetStream();
            FrameCodec.WriteFrame(stream, new byte[] { 0xFF });   // too short to be a message

            PumpFor(TimeSpan.FromMilliseconds(600), host);

            Assert.Empty(host.Peers);
            Assert.Equal(SessionState.Active, host.State);   // host is unharmed
        }

        [Fact]
        public void TwoClientsBothJoin()
        {
            var port = FreePort();
            var host = NewSession();
            host.Host(new TcpTransport(), port.ToString(), Identity("Hana"));

            var a = NewSession();
            a.Join(new TcpTransport(), $"127.0.0.1:{port}", Identity("Rania"));
            var b = NewSession();
            b.Join(new TcpTransport(), $"127.0.0.1:{port}", Identity("Bo"));

            PumpUntil(() => host.Peers.Count == 2 && a.IsActive && b.IsActive, host, a, b);

            Assert.Equal(2, host.Peers.Count);
            Assert.NotEqual(a.LocalPeer, b.LocalPeer);
        }

        // ------------------------------------------------------------ helpers

        private NightshareSession NewSession()
        {
            var s = new NightshareSession();
            _sessions.Add(s);
            return s;
        }

        private static SessionIdentity Identity(string name, string mod = Mod, string build = Build) =>
            new SessionIdentity { PlayerName = name, ModVersion = mod, GameBuildId = build };

        private (NightshareSession host, NightshareSession client) Pair(
            string hostName = "Hana", string clientName = "Rania")
        {
            var port = FreePort();
            var host = NewSession();
            host.Host(new TcpTransport(), port.ToString(), Identity(hostName));

            var client = NewSession();
            client.Join(new TcpTransport(), $"127.0.0.1:{port}", Identity(clientName));

            return (host, client);
        }

        private static void PumpUntil(Func<bool> condition, params NightshareSession[] sessions)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < Timeout)
            {
                foreach (var s in sessions) s.Pump();
                if (condition()) return;
                Thread.Sleep(5);
            }
            throw new TimeoutException($"Condition not met within {Timeout.TotalSeconds}s.");
        }

        private static void PumpFor(TimeSpan duration, params NightshareSession[] sessions)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration)
            {
                foreach (var s in sessions) s.Pump();
                Thread.Sleep(5);
            }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
