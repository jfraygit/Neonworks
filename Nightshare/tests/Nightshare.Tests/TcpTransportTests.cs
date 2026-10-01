using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nightshare.Core;
using Nightshare.Core.Transport;
using Xunit;

namespace Nightshare.Tests
{
    /// <summary>
    /// Integration tests over real loopback sockets. These are the tests that matter:
    /// a transport that compiles proves nothing.
    /// </summary>
    public class TcpTransportTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private readonly List<INetTransport> _toDispose = new();

        public void Dispose()
        {
            foreach (var t in _toDispose)
            {
                try { t.Dispose(); } catch { }
            }
        }

        [Fact]
        public void HostAndClientBothObserveTheConnection()
        {
            var (host, client, clientPeerOnHost) = ConnectedPair();

            Assert.True(host.IsHost);
            Assert.False(client.IsHost);
            Assert.True(clientPeerOnHost.IsValid);
            Assert.Contains(clientPeerOnHost, host.ConnectedPeers);
            Assert.Contains(PeerId.Host, client.ConnectedPeers);
        }

        [Fact]
        public void HostReachesTheClient()
        {
            var (host, client, clientPeer) = ConnectedPair();

            host.Send(clientPeer, new byte[] { 1, 2, 3 });

            var evt = WaitFor(client, TransportEventKind.Data);
            Assert.Equal(new byte[] { 1, 2, 3 }, evt.Payload);
            Assert.Equal(PeerId.Host, evt.Peer);
        }

        [Fact]
        public void ClientReachesTheHost()
        {
            var (host, client, clientPeer) = ConnectedPair();

            client.Send(PeerId.Host, new byte[] { 9, 8, 7 });

            var evt = WaitFor(host, TransportEventKind.Data);
            Assert.Equal(new byte[] { 9, 8, 7 }, evt.Payload);
            Assert.Equal(clientPeer, evt.Peer);
        }

        [Fact]
        public void MessagesArriveInOrder()
        {
            var (host, client, clientPeer) = ConnectedPair();

            for (int i = 0; i < 200; i++)
                host.Send(clientPeer, BitConverter.GetBytes(i));

            for (int i = 0; i < 200; i++)
            {
                var evt = WaitFor(client, TransportEventKind.Data);
                Assert.Equal(i, BitConverter.ToInt32(evt.Payload, 0));
            }
        }

        /// <summary>
        /// A world snapshot measured about 600 KB on a day-one save. This proves a payload
        /// far larger than one TCP segment survives the framing intact.
        /// </summary>
        [Fact]
        public void CarriesAWorldSnapshotSizedPayload()
        {
            var (host, client, clientPeer) = ConnectedPair();

            var snapshot = new byte[600 * 1024];
            new Random(1234).NextBytes(snapshot);

            host.Send(clientPeer, snapshot);

            var evt = WaitFor(client, TransportEventKind.Data);
            Assert.Equal(snapshot.Length, evt.Payload.Length);
            Assert.Equal(snapshot, evt.Payload);
        }

        [Fact]
        public void BroadcastReachesEveryClientButTheExcludedOne()
        {
            var port = FreePort();
            var host = Host(port);

            var a = Client(port);
            var peerA = WaitFor(host, TransportEventKind.PeerConnected).Peer;
            var b = Client(port);
            var peerB = WaitFor(host, TransportEventKind.PeerConnected).Peer;

            WaitFor(a, TransportEventKind.PeerConnected);
            WaitFor(b, TransportEventKind.PeerConnected);

            host.Broadcast(new byte[] { 42 }, DeliveryMode.Reliable, except: peerA);

            Assert.Equal(new byte[] { 42 }, WaitFor(b, TransportEventKind.Data).Payload);
            Assert.False(TryWaitFor(a, TransportEventKind.Data, TimeSpan.FromMilliseconds(300), out _),
                "the excluded peer should not have received the broadcast");
            Assert.NotEqual(peerA, peerB);
        }

        [Fact]
        public void HostSeesTheClientLeave()
        {
            var (host, client, clientPeer) = ConnectedPair();

            client.Stop();

            var evt = WaitFor(host, TransportEventKind.PeerDisconnected);
            Assert.Equal(clientPeer, evt.Peer);
            Assert.DoesNotContain(clientPeer, host.ConnectedPeers);
        }

        [Fact]
        public void ConnectingToNothingReportsAnErrorRatherThanHanging()
        {
            var client = new TcpTransport();
            _toDispose.Add(client);

            client.Connect($"127.0.0.1:{FreePort()}");

            var evt = WaitFor(client, TransportEventKind.Error);
            Assert.Contains("Could not connect", evt.Message);
        }

        [Fact]
        public void EachClientGetsADistinctPeerId()
        {
            var port = FreePort();
            var host = Host(port);

            Client(port);
            var first = WaitFor(host, TransportEventKind.PeerConnected).Peer;
            Client(port);
            var second = WaitFor(host, TransportEventKind.PeerConnected).Peer;

            Assert.NotEqual(first, second);
            Assert.True(first.IsValid);
            Assert.True(second.IsValid);
        }

        // ------------------------------------------------------------------ helpers

        private (INetTransport host, INetTransport client, PeerId clientPeer) ConnectedPair()
        {
            var port = FreePort();
            var host = Host(port);
            var client = Client(port);

            var clientPeer = WaitFor(host, TransportEventKind.PeerConnected).Peer;
            WaitFor(client, TransportEventKind.PeerConnected);

            return (host, client, clientPeer);
        }

        private INetTransport Host(int port)
        {
            var t = new TcpTransport();
            _toDispose.Add(t);
            t.StartHost(port.ToString());
            return t;
        }

        private INetTransport Client(int port)
        {
            var t = new TcpTransport();
            _toDispose.Add(t);
            t.Connect($"127.0.0.1:{port}");
            return t;
        }

        private static TransportEvent WaitFor(INetTransport t, TransportEventKind kind)
        {
            if (TryWaitFor(t, kind, Timeout, out var evt)) return evt;
            throw new TimeoutException($"No {kind} event within {Timeout.TotalSeconds}s.");
        }

        private static bool TryWaitFor(INetTransport t, TransportEventKind kind, TimeSpan timeout, out TransportEvent found)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                while (t.TryDequeueEvent(out var evt))
                {
                    if (evt.Kind == kind) { found = evt; return true; }
                }
                Thread.Sleep(5);
            }
            found = default;
            return false;
        }

        /// <summary>Ask the OS for an unused port, then hand it back.</summary>
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
