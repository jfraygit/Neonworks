using System;

namespace Nightshare.Core.Transport
{
    /// <summary>Which concrete transport is in use.</summary>
    public enum TransportKind
    {
        /// <summary>In-process, no sockets. Unit tests.</summary>
        Loopback,

        /// <summary>TCP over loopback or LAN. Solo testing and development.</summary>
        Tcp,

        /// <summary>Facepunch SteamNetworkingSockets. Real play.</summary>
        SteamP2P,
    }

    /// <summary>
    /// How a payload should be delivered. TCP treats everything as reliable ordered;
    /// the distinction only becomes real on the Steam transport.
    /// </summary>
    public enum DeliveryMode
    {
        /// <summary>Guaranteed, in order. Handshakes, transactions, state snapshots.</summary>
        Reliable = 0,

        /// <summary>May be dropped or reordered. Position updates, where the next one supersedes.</summary>
        Unreliable = 1,
    }

    public enum TransportEventKind
    {
        PeerConnected,
        PeerDisconnected,
        Data,
        Error,
    }

    /// <summary>
    /// Something the transport wants the game to know about. Events are queued by the IO
    /// thread and drained by the caller; see <see cref="INetTransport.TryDequeueEvent"/>.
    /// </summary>
    public readonly struct TransportEvent
    {
        public TransportEventKind Kind { get; }
        public PeerId Peer { get; }

        /// <summary>Payload for <see cref="TransportEventKind.Data"/>. Owned by the receiver.</summary>
        public byte[] Payload { get; }

        /// <summary>Human-readable reason for Disconnected and Error.</summary>
        public string Message { get; }

        public TransportEvent(TransportEventKind kind, PeerId peer, byte[] payload, string message)
        {
            Kind = kind;
            Peer = peer;
            Payload = payload;
            Message = message;
        }

        public static TransportEvent Connected(PeerId peer) =>
            new TransportEvent(TransportEventKind.PeerConnected, peer, null, null);

        public static TransportEvent Disconnected(PeerId peer, string reason) =>
            new TransportEvent(TransportEventKind.PeerDisconnected, peer, null, reason);

        public static TransportEvent Data(PeerId peer, byte[] payload) =>
            new TransportEvent(TransportEventKind.Data, peer, payload, null);

        public static TransportEvent Error(string message, PeerId peer = default) =>
            new TransportEvent(TransportEventKind.Error, peer, null, message);

        public override string ToString() => Kind switch
        {
            TransportEventKind.Data => $"Data from {Peer.ToShortString()} ({Payload?.Length ?? 0} bytes)",
            TransportEventKind.PeerConnected => $"Connected {Peer.ToShortString()}",
            TransportEventKind.PeerDisconnected => $"Disconnected {Peer.ToShortString()}: {Message}",
            _ => $"Error: {Message}",
        };
    }

    /// <summary>
    /// Moves opaque byte payloads between peers. Knows nothing about game state, message
    /// types or session rules; those live above it.
    /// <para>
    /// <b>Threading contract.</b> Implementations do their IO on background threads but
    /// must never invoke anything on the caller's behalf from those threads. All inbound
    /// activity surfaces through <see cref="TryDequeueEvent"/>, which the game drains on
    /// the Unity main thread. Calling a Unity API off the main thread is a crash, so this
    /// is not a style preference.
    /// </para>
    /// </summary>
    public interface INetTransport : IDisposable
    {
        TransportKind Kind { get; }

        /// <summary>True once <see cref="StartHost"/> or <see cref="Connect"/> has succeeded.</summary>
        bool IsRunning { get; }

        /// <summary>True on the peer that owns the world.</summary>
        bool IsHost { get; }

        /// <summary>This peer's own id. Assigned locally on the host, received in the handshake on a client.</summary>
        PeerId LocalPeer { get; }

        /// <summary>Begin listening. Throws if already running.</summary>
        void StartHost(string endpoint);

        /// <summary>Begin connecting. Completion arrives as a PeerConnected event, not synchronously.</summary>
        void Connect(string endpoint);

        /// <summary>Stop listening or disconnect. Safe to call when not running.</summary>
        void Stop();

        /// <summary>Send to one peer. Silently drops if the peer is unknown or gone.</summary>
        void Send(PeerId target, byte[] payload, DeliveryMode mode = DeliveryMode.Reliable);

        /// <summary>Send to every connected peer except <paramref name="except"/>.</summary>
        void Broadcast(byte[] payload, DeliveryMode mode = DeliveryMode.Reliable, PeerId except = default);

        /// <summary>
        /// Close one peer's connection. Used when a peer is refused or proves untrustworthy.
        /// Queued sends are flushed first, so a Reject message sent immediately before this
        /// still reaches them. No-op for an unknown peer.
        /// </summary>
        void Disconnect(PeerId peer);

        /// <summary>
        /// Drain one queued event. Call in a loop from the main thread every frame until
        /// it returns false.
        /// </summary>
        bool TryDequeueEvent(out TransportEvent evt);

        /// <summary>Currently connected peers, not including this one.</summary>
        PeerId[] ConnectedPeers { get; }
    }
}
