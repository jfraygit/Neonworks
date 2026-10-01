using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nightshare.Core.Protocol;
using Nightshare.Core.Transport;

namespace Nightshare.Core.Session
{
    /// <summary>
    /// Owns a transport and runs the handshake, peer bookkeeping and message dispatch.
    /// <para>
    /// This type is shared by the game plugin and by the standalone fake peer, which is
    /// the whole point: the fake peer exercises the real session logic rather than a
    /// lookalike, so a protocol bug it finds is a bug the game would have had.
    /// </para>
    /// <para>
    /// Nothing here touches Unity. <see cref="Pump"/> must be called from the game's main
    /// thread; every event fires inside that call.
    /// </para>
    /// </summary>
    public sealed class NightshareSession : IDisposable
    {
        /// <summary>How long a peer may take to complete its handshake before being dropped.</summary>
        public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

        private readonly Dictionary<PeerId, RemotePeer> _peers = new Dictionary<PeerId, RemotePeer>();
        private readonly Dictionary<PeerId, Stopwatch> _pendingHandshakes = new Dictionary<PeerId, Stopwatch>();

        private INetTransport _transport;
        private SessionIdentity _identity;

        public SessionRole Role { get; private set; } = SessionRole.None;
        public SessionState State { get; private set; } = SessionState.Idle;

        /// <summary>Set when <see cref="State"/> is <see cref="SessionState.Failed"/>.</summary>
        public string FailureReason { get; private set; }

        /// <summary>Our own id. On a client this is provisional until Welcome arrives.</summary>
        public PeerId LocalPeer { get; private set; }

        public string SessionId { get; private set; }

        public IReadOnlyCollection<RemotePeer> Peers => _peers.Values;

        public bool IsHost => Role == SessionRole.Host;

        /// <summary>The host's player name, or "Host" before the handshake names them.</summary>
        public string HostName
        {
            get
            {
                foreach (var p in _peers.Values)
                    if (p.IsHost && !string.IsNullOrEmpty(p.Name)) return p.Name;

                return "Host";
            }
        }
        public bool IsActive => State == SessionState.Active;

        // --- events, all raised inside Pump on the calling thread ---

        /// <summary>A peer finished its handshake and is addressable.</summary>
        public event Action<RemotePeer> PeerJoined;

        /// <summary>A peer went away. The string is a human-readable reason.</summary>
        public event Action<RemotePeer, string> PeerLeft;

        /// <summary>This session became <see cref="SessionState.Active"/>.</summary>
        public event Action SessionEstablished;

        /// <summary>The session failed. The string is safe to show a player.</summary>
        public event Action<string> SessionFailed;

        /// <summary>
        /// A message this layer does not handle itself. The reader is positioned after the
        /// type header and is disposed once the handler returns, so do not keep it.
        /// </summary>
        public event Action<PeerId, NetReader> MessageReceived;

        /// <summary>Diagnostics. Wire to the BepInEx logger in game, to the console in the fake peer.</summary>
        public event Action<string> Log;

        // ---------------------------------------------------------------- lifecycle

        public void Host(INetTransport transport, string endpoint, SessionIdentity identity)
        {
            Begin(transport, identity, SessionRole.Host);

            SessionId = string.IsNullOrEmpty(identity.SessionId)
                ? Guid.NewGuid().ToString("N")
                : identity.SessionId;

            LocalPeer = PeerId.Host;

            try
            {
                _transport.StartHost(endpoint);
                State = SessionState.Active;      // a host is immediately live, with nobody in it
                Emit($"Hosting on {endpoint} as '{identity.PlayerName}', session {SessionId}");
                SessionEstablished?.Invoke();
            }
            catch (Exception ex)
            {
                Fail($"Could not start hosting on {endpoint}: {ex.Message}");
            }
        }

        public void Join(INetTransport transport, string endpoint, SessionIdentity identity)
        {
            Begin(transport, identity, SessionRole.Client);

            LocalPeer = PeerId.NewId();          // provisional, replaced by Welcome
            State = SessionState.Connecting;

            try
            {
                _transport.Connect(endpoint);
                Emit($"Connecting to {endpoint} as '{identity.PlayerName}'");
            }
            catch (Exception ex)
            {
                Fail($"Could not connect to {endpoint}: {ex.Message}");
            }
        }

        private void Begin(INetTransport transport, SessionIdentity identity, SessionRole role)
        {
            if (State != SessionState.Idle && State != SessionState.Closed && State != SessionState.Failed)
                throw new InvalidOperationException($"Session is already {State}.");

            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Role = role;
            FailureReason = null;
            _peers.Clear();
            _pendingHandshakes.Clear();
        }

        public void Leave()
        {
            if (_transport == null) return;

            try { _transport.Stop(); } catch { }
            _peers.Clear();
            _pendingHandshakes.Clear();

            if (State != SessionState.Failed) State = SessionState.Closed;
            Role = SessionRole.None;
            Emit("Session closed");
        }

        public void Dispose()
        {
            try { Leave(); } catch { }
            try { _transport?.Dispose(); } catch { }
            _transport = null;
        }

        // ---------------------------------------------------------------- main loop

        /// <summary>
        /// Drain the transport and dispatch. Call every frame from the main thread.
        /// Never throws; a handler that faults is logged and the session carries on.
        /// </summary>
        public void Pump()
        {
            if (_transport == null) return;

            while (_transport.TryDequeueEvent(out var evt))
            {
                try
                {
                    Dispatch(evt);
                }
                catch (Exception ex)
                {
                    Emit($"Error handling {evt.Kind}: {ex.Message}");
                }
            }

            ExpireStaleHandshakes();
        }

        private void Dispatch(TransportEvent evt)
        {
            switch (evt.Kind)
            {
                case TransportEventKind.PeerConnected:
                    OnPeerConnected(evt.Peer);
                    break;

                case TransportEventKind.PeerDisconnected:
                    OnPeerDisconnected(evt.Peer, evt.Message);
                    break;

                case TransportEventKind.Data:
                    OnData(evt.Peer, evt.Payload);
                    break;

                case TransportEventKind.Error:
                    Fail(evt.Message);
                    break;
            }
        }

        private void OnPeerConnected(PeerId peer)
        {
            if (IsHost)
            {
                // Wait for their Hello before deciding anything about them.
                _pendingHandshakes[peer] = Stopwatch.StartNew();
                Emit($"Peer {peer.ToShortString()} connected, awaiting handshake");
            }
            else
            {
                State = SessionState.Handshaking;
                _pendingHandshakes[peer] = Stopwatch.StartNew();

                var hello = new HelloV1
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    ModVersion = _identity.ModVersion,
                    GameBuildId = _identity.GameBuildId,
                    PlayerName = _identity.PlayerName,
                };
                _transport.Send(peer, hello.Serialise());
                Emit("Sent Hello, awaiting Welcome");
            }
        }

        private void OnPeerDisconnected(PeerId peer, string reason)
        {
            _pendingHandshakes.Remove(peer);

            if (_peers.TryGetValue(peer, out var remote))
            {
                _peers.Remove(peer);
                Emit($"{remote} left: {reason}");
                PeerLeft?.Invoke(remote, reason);
            }

            // Losing the host ends a client's session; losing a guest does not end a host's.
            if (!IsHost && State != SessionState.Failed && State != SessionState.Closed)
                Fail($"Disconnected from host: {reason}");
        }

        private void OnData(PeerId peer, byte[] payload)
        {
            NetReader reader;
            try
            {
                reader = new NetReader(payload);
            }
            catch (MalformedMessageException ex)
            {
                DropPeer(peer, $"sent an unreadable message: {ex.Message}");
                return;
            }

            using (reader)
            {
                if (reader.IsUnknownType)
                {
                    // Forward compatible by design: a newer peer may send types we have no
                    // handler for. Ignore them rather than dropping the connection.
                    Emit($"Ignoring unknown message type {reader.RawType} from {peer.ToShortString()}");
                    return;
                }

                try
                {
                    switch (reader.Type)
                    {
                        case MessageType.HelloV1:   HandleHello(peer, reader);   return;
                        case MessageType.WelcomeV1: HandleWelcome(peer, reader); return;
                        case MessageType.RejectV1:  HandleReject(reader);        return;
                        default:
                            MessageReceived?.Invoke(peer, reader);
                            return;
                    }
                }
                catch (MalformedMessageException ex)
                {
                    DropPeer(peer, $"sent a malformed {reader.Type}: {ex.Message}");
                }
            }
        }

        // ---------------------------------------------------------------- handshake

        private void HandleHello(PeerId peer, NetReader reader)
        {
            if (!IsHost)
            {
                DropPeer(peer, "a client may not send Hello to another client");
                return;
            }

            var hello = HelloV1.Parse(reader);

            var problem = ProtocolVersion.CheckCompatibility(
                hello.ProtocolVersion, hello.ModVersion, hello.GameBuildId,
                _identity.ModVersion, _identity.GameBuildId);

            if (problem != null)
            {
                var reason = hello.ProtocolVersion != ProtocolVersion.Current
                    ? RejectReason.ProtocolMismatch
                    : hello.ModVersion != _identity.ModVersion
                        ? RejectReason.ModVersionMismatch
                        : RejectReason.GameVersionMismatch;

                Emit($"Refusing {hello.PlayerName}: {problem}");
                _transport.Send(peer, new RejectV1 { Reason = reason, Detail = problem }.Serialise());

                // Give the frame a chance to leave before the socket closes.
                DropPeer(peer, problem, notifyPeer: false);
                return;
            }

            _pendingHandshakes.Remove(peer);

            var remote = new RemotePeer(peer, hello.PlayerName, isHost: false) { IsReady = true };
            _peers[peer] = remote;

            _transport.Send(peer, new WelcomeV1
            {
                AssignedPeerId = peer,
                HostPlayerName = _identity.PlayerName,
                SessionId = SessionId,
                TotalGameSeconds = CurrentGameSeconds,
                GameplayGameDay = CurrentGameDay,
            }.Serialise());

            Emit($"{remote} joined");
            PeerJoined?.Invoke(remote);
        }

        private void HandleWelcome(PeerId peer, NetReader reader)
        {
            if (IsHost)
            {
                DropPeer(peer, "a client may not send Welcome to the host");
                return;
            }

            var welcome = WelcomeV1.Parse(reader);

            _pendingHandshakes.Remove(peer);

            LocalPeer = welcome.AssignedPeerId;
            SessionId = welcome.SessionId;
            State = SessionState.Active;

            var host = new RemotePeer(peer, welcome.HostPlayerName, isHost: true) { IsReady = true };
            _peers[peer] = host;

            Emit($"Joined {welcome.HostPlayerName}'s session as {LocalPeer.ToShortString()}, " +
                 $"day {welcome.GameplayGameDay} at {welcome.TotalGameSeconds}s");

            PeerJoined?.Invoke(host);
            SessionEstablished?.Invoke();
        }

        private void HandleReject(NetReader reader)
        {
            var reject = RejectV1.Parse(reader);
            Fail(reject.Detail);
        }

        private void ExpireStaleHandshakes()
        {
            if (_pendingHandshakes.Count == 0) return;

            List<PeerId> stale = null;
            foreach (var kv in _pendingHandshakes)
            {
                if (kv.Value.Elapsed > HandshakeTimeout)
                    (stale ??= new List<PeerId>()).Add(kv.Key);
            }

            if (stale == null) return;
            foreach (var peer in stale)
                DropPeer(peer, $"did not complete a handshake within {HandshakeTimeout.TotalSeconds:0}s");
        }

        // ---------------------------------------------------------------- sending

        public void Send(PeerId target, byte[] payload, DeliveryMode mode = DeliveryMode.Reliable)
            => _transport?.Send(target, payload, mode);

        public void Broadcast(byte[] payload, DeliveryMode mode = DeliveryMode.Reliable, PeerId except = default)
            => _transport?.Broadcast(payload, mode, except);

        /// <summary>Send to the host. Only meaningful on a client.</summary>
        public void SendToHost(byte[] payload, DeliveryMode mode = DeliveryMode.Reliable)
        {
            foreach (var p in _peers.Values)
            {
                if (p.IsHost) { Send(p.Id, payload, mode); return; }
            }
        }

        // ---------------------------------------------------------------- clock hooks

        /// <summary>
        /// Supplies the host clock for the Welcome message. The plugin points this at
        /// TimeOfDayManager; the fake peer leaves it alone.
        /// </summary>
        public Func<int> GameSecondsProvider { get; set; }

        /// <summary>As <see cref="GameSecondsProvider"/>, for the day counter.</summary>
        public Func<int> GameDayProvider { get; set; }

        private int CurrentGameSeconds => SafeInvoke(GameSecondsProvider);
        private int CurrentGameDay => SafeInvoke(GameDayProvider);

        private int SafeInvoke(Func<int> f)
        {
            if (f == null) return 0;
            try { return f(); }
            catch (Exception ex) { Emit($"Clock provider threw: {ex.Message}"); return 0; }
        }

        // ---------------------------------------------------------------- helpers

        private void DropPeer(PeerId peer, string reason, bool notifyPeer = true)
        {
            Emit($"Dropping {peer.ToShortString()}: {reason}");

            if (notifyPeer)
            {
                try
                {
                    _transport?.Send(peer, new RejectV1
                    {
                        Reason = RejectReason.MalformedHandshake,
                        Detail = reason,
                    }.Serialise());
                }
                catch { }
            }

            _pendingHandshakes.Remove(peer);
            if (_peers.TryGetValue(peer, out var remote))
            {
                _peers.Remove(peer);
                PeerLeft?.Invoke(remote, reason);
            }

            // Close after flushing so the Reject above actually reaches them.
            try { _transport?.Disconnect(peer); } catch { }
        }

        private void Fail(string reason)
        {
            if (State == SessionState.Failed) return;

            State = SessionState.Failed;
            FailureReason = reason;
            Emit($"Session failed: {reason}");
            SessionFailed?.Invoke(reason);
        }

        private void Emit(string message) => Log?.Invoke(message);
    }
}
