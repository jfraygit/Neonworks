using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nightshare.Core.Transport
{
    /// <summary>
    /// TCP transport over loopback or LAN.
    /// <para>
    /// <b>This is load bearing, not a development convenience.</b> Two copies of Nivalis
    /// Nights on one machine attach to the same Steam client and report the same SteamID.
    /// Anything keyed on SteamID therefore cannot tell the two instances apart, which makes
    /// solo testing impossible. Running the session over TCP with mod-assigned
    /// <see cref="PeerId"/>s sidesteps that completely.
    /// </para>
    /// <para>
    /// Endpoint format is <c>host:port</c>, for example <c>127.0.0.1:7777</c>. A host may
    /// pass just a port.
    /// </para>
    /// </summary>
    public sealed class TcpTransport : INetTransport
    {
        private const int DefaultPort = 7777;

        private readonly ConcurrentQueue<TransportEvent> _events = new ConcurrentQueue<TransportEvent>();
        private readonly ConcurrentDictionary<PeerId, Connection> _connections = new ConcurrentDictionary<PeerId, Connection>();

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Thread _acceptThread;
        private volatile bool _running;

        public TransportKind Kind => TransportKind.Tcp;
        public bool IsRunning => _running;
        public bool IsHost { get; private set; }
        public PeerId LocalPeer { get; private set; }

        public PeerId[] ConnectedPeers
        {
            get
            {
                var keys = new List<PeerId>(_connections.Count);
                foreach (var kv in _connections) keys.Add(kv.Key);
                return keys.ToArray();
            }
        }

        // ---------------------------------------------------------------- lifecycle

        public void StartHost(string endpoint)
        {
            if (_running) throw new InvalidOperationException("Transport is already running.");

            var (address, port) = ParseEndpoint(endpoint, IPAddress.Any);

            IsHost = true;
            LocalPeer = PeerId.Host;
            _cts = new CancellationTokenSource();

            _listener = new TcpListener(address, port);
            _listener.Start();
            _running = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "Nightshare TCP accept",
            };
            _acceptThread.Start();
        }

        public void Connect(string endpoint)
        {
            if (_running) throw new InvalidOperationException("Transport is already running.");

            var (address, port) = ParseEndpoint(endpoint, IPAddress.Loopback);

            IsHost = false;
            LocalPeer = PeerId.NewId();
            _cts = new CancellationTokenSource();
            _running = true;

            // Connect off the calling thread; a failed connect can block for a long time
            // and the game must not stall on it.
            var thread = new Thread(() => ConnectLoop(address, port))
            {
                IsBackground = true,
                Name = "Nightshare TCP connect",
            };
            thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }

            foreach (var kv in _connections) kv.Value.Close();
            _connections.Clear();

            _listener = null;
        }

        public void Dispose()
        {
            Stop();
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }

        // ---------------------------------------------------------------- sending

        public void Send(PeerId target, byte[] payload, DeliveryMode mode = DeliveryMode.Reliable)
        {
            // TCP is reliable ordered by construction, so mode is recorded for the Steam
            // transport's benefit and otherwise ignored here.
            if (!_connections.TryGetValue(target, out var conn)) return;
            conn.Enqueue(payload);
        }

        public void Broadcast(byte[] payload, DeliveryMode mode = DeliveryMode.Reliable, PeerId except = default)
        {
            foreach (var kv in _connections)
            {
                if (kv.Key == except) continue;
                kv.Value.Enqueue(payload);
            }
        }

        public void Disconnect(PeerId peer)
        {
            if (_connections.TryGetValue(peer, out var conn))
                conn.CloseAfterFlush();
        }

        public bool TryDequeueEvent(out TransportEvent evt) => _events.TryDequeue(out evt);

        // ---------------------------------------------------------------- IO threads

        private void AcceptLoop()
        {
            var token = _cts.Token;
            while (_running && !token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException) { break; }          // listener stopped
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }

                // The host mints the id. The client learns it in the handshake, which is a
                // protocol concern one layer up.
                var peer = PeerId.NewId();
                StartConnection(peer, client);
            }
        }

        private void ConnectLoop(IPAddress address, int port)
        {
            try
            {
                var client = new TcpClient();
                client.Connect(address, port);
                StartConnection(PeerId.Host, client);
            }
            catch (Exception ex)
            {
                _running = false;
                _events.Enqueue(TransportEvent.Error($"Could not connect to {address}:{port}: {ex.Message}"));
            }
        }

        private void StartConnection(PeerId peer, TcpClient client)
        {
            client.NoDelay = true;          // this is a game, latency beats throughput

            var conn = new Connection(peer, client, _events, () => _connections.TryRemove(peer, out _));
            _connections[peer] = conn;
            conn.Start(_cts.Token);

            _events.Enqueue(TransportEvent.Connected(peer));
        }

        // ---------------------------------------------------------------- helpers

        private static (IPAddress, int) ParseEndpoint(string endpoint, IPAddress fallbackAddress)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return (fallbackAddress, DefaultPort);

            // Bare port, e.g. "7777"
            if (int.TryParse(endpoint, out var bare))
                return (fallbackAddress, bare);

            var idx = endpoint.LastIndexOf(':');
            if (idx < 0)
                return (ResolveHost(endpoint, fallbackAddress), DefaultPort);

            var hostPart = endpoint.Substring(0, idx);
            var portPart = endpoint.Substring(idx + 1);

            if (!int.TryParse(portPart, out var port)) port = DefaultPort;
            return (ResolveHost(hostPart, fallbackAddress), port);
        }

        private static IPAddress ResolveHost(string host, IPAddress fallback)
        {
            if (string.IsNullOrWhiteSpace(host)) return fallback;
            if (host == "*" || host == "0.0.0.0" || host.Equals("any", StringComparison.OrdinalIgnoreCase))
                return IPAddress.Any;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return IPAddress.Loopback;
            if (IPAddress.TryParse(host, out var parsed)) return parsed;

            var entries = Dns.GetHostAddresses(host);
            foreach (var e in entries)
                if (e.AddressFamily == AddressFamily.InterNetwork) return e;

            return fallback;
        }

        /// <summary>
        /// One peer's socket, with a dedicated reader thread and a dedicated writer thread.
        /// A single writer thread per connection is what makes <see cref="INetTransport.Send"/>
        /// safe to call from anywhere without locking the stream at every call site.
        /// </summary>
        private sealed class Connection
        {
            private readonly PeerId _peer;
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private readonly ConcurrentQueue<TransportEvent> _events;
            private readonly Action _onClosed;

            private readonly BlockingCollection<byte[]> _outbound =
                new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());

            private int _closed;

            public Connection(PeerId peer, TcpClient client, ConcurrentQueue<TransportEvent> events, Action onClosed)
            {
                _peer = peer;
                _client = client;
                _stream = client.GetStream();
                _events = events;
                _onClosed = onClosed;
            }

            public void Start(CancellationToken token)
            {
                new Thread(() => ReadLoop(token))
                {
                    IsBackground = true,
                    Name = $"Nightshare read {_peer.ToShortString()}",
                }.Start();

                new Thread(() => WriteLoop(token))
                {
                    IsBackground = true,
                    Name = $"Nightshare write {_peer.ToShortString()}",
                }.Start();
            }

            public void Enqueue(byte[] payload)
            {
                if (Volatile.Read(ref _closed) != 0) return;
                try { _outbound.Add(payload); } catch (InvalidOperationException) { }
            }

            private void ReadLoop(CancellationToken token)
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var frame = FrameCodec.ReadFrame(_stream, token);
                        if (frame == null) break;               // clean close
                        _events.Enqueue(TransportEvent.Data(_peer, frame));
                    }
                    CloseWith("remote closed the connection");
                }
                catch (InvalidDataException ex)
                {
                    // A bad length prefix means the stream is no longer trustworthy.
                    // Never try to resynchronise; drop the peer and say why.
                    CloseWith($"protocol error: {ex.Message}");
                }
                catch (Exception ex)
                {
                    CloseWith($"read failed: {ex.Message}");
                }
            }

            private void WriteLoop(CancellationToken token)
            {
                try
                {
                    foreach (var payload in _outbound.GetConsumingEnumerable(token))
                        FrameCodec.WriteFrame(_stream, payload);

                    // The queue was completed and has now drained, so this is a
                    // CloseAfterFlush finishing its job.
                    CloseWith("closed locally");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    CloseWith($"write failed: {ex.Message}");
                }
            }

            public void Close() => CloseWith("closed locally");

            /// <summary>
            /// Stop accepting new sends and close once the queue has drained, so a Reject
            /// written immediately beforehand still reaches the peer. Closing the socket
            /// outright would discard it and the player would see a bare disconnect with
            /// no explanation.
            /// </summary>
            public void CloseAfterFlush()
            {
                try { _outbound.CompleteAdding(); } catch { }
            }

            private void CloseWith(string reason)
            {
                // Both loops can land here; only the first one reports.
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;

                try { _outbound.CompleteAdding(); } catch { }
                try { _stream?.Close(); } catch { }
                try { _client?.Close(); } catch { }

                _onClosed?.Invoke();
                _events.Enqueue(TransportEvent.Disconnected(_peer, reason));
            }
        }
    }
}
