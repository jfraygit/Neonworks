using System;

namespace Nightshare.Core.Session
{
    public enum SessionRole
    {
        None = 0,
        Host = 1,
        Client = 2,
    }

    public enum SessionState
    {
        /// <summary>Nothing running.</summary>
        Idle = 0,

        /// <summary>Transport is dialling. No peer yet.</summary>
        Connecting = 1,

        /// <summary>Connected, exchanging Hello and Welcome.</summary>
        Handshaking = 2,

        /// <summary>Handshake complete. Game traffic may flow.</summary>
        Active = 3,

        /// <summary>Stopped by a failure. <see cref="NightshareSession.FailureReason"/> says why.</summary>
        Failed = 4,

        /// <summary>Stopped deliberately.</summary>
        Closed = 5,
    }

    /// <summary>Who we are, sent in the handshake so the other side can vet us.</summary>
    public sealed class SessionIdentity
    {
        public string PlayerName { get; set; } = "Player";
        public string ModVersion { get; set; } = "0.0.0";
        public string GameBuildId { get; set; } = "unknown";

        /// <summary>Host only. Identifies this world so a returning guest can be recognised.</summary>
        public string SessionId { get; set; } = "";
    }

    /// <summary>Another participant, once their handshake has completed.</summary>
    public sealed class RemotePeer
    {
        public RemotePeer(PeerId id, string name, bool isHost)
        {
            Id = id;
            Name = name;
            IsHost = isHost;
        }

        public PeerId Id { get; }
        public string Name { get; }
        public bool IsHost { get; }

        /// <summary>Set once the handshake finished. Pending peers are not yet addressable.</summary>
        public bool IsReady { get; internal set; }

        public override string ToString() => $"{Name} ({Id.ToShortString()}){(IsHost ? " [host]" : "")}";
    }
}
