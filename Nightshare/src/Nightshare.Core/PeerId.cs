using System;

namespace Nightshare.Core
{
    /// <summary>
    /// Identity of a participant in a session.
    /// <para>
    /// This is a mod-assigned GUID and is deliberately NOT a SteamID. Two copies of the
    /// game running on one machine attach to the same Steam client and report the same
    /// SteamID, so any SteamID-keyed protocol breaks the moment you try to test alone.
    /// Keying on our own id makes solo testing work and costs nothing in real play.
    /// </para>
    /// </summary>
    public readonly struct PeerId : IEquatable<PeerId>
    {
        /// <summary>Not a valid participant. Also the "unassigned" value.</summary>
        public static readonly PeerId None = default;

        /// <summary>
        /// The host always holds this id. Fixed so a client can address the host before
        /// the handshake has told it anything.
        /// </summary>
        public static readonly PeerId Host =
            new PeerId(new Guid("00000000-0000-0000-0000-000000000001"));

        private readonly Guid _value;

        public PeerId(Guid value) => _value = value;

        public Guid Value => _value;

        public bool IsValid => _value != Guid.Empty;

        public bool IsHost => _value == Host._value;

        public static PeerId NewId() => new PeerId(Guid.NewGuid());

        public static PeerId Parse(string s) => new PeerId(Guid.Parse(s));

        public static bool TryParse(string s, out PeerId id)
        {
            if (Guid.TryParse(s, out var g)) { id = new PeerId(g); return true; }
            id = None;
            return false;
        }

        public bool Equals(PeerId other) => _value.Equals(other._value);

        public override bool Equals(object obj) => obj is PeerId other && Equals(other);

        public override int GetHashCode() => _value.GetHashCode();

        public static bool operator ==(PeerId a, PeerId b) => a.Equals(b);

        public static bool operator !=(PeerId a, PeerId b) => !a.Equals(b);

        /// <summary>Full round-trippable form.</summary>
        public override string ToString() => _value.ToString();

        /// <summary>Short form for logs. Never use this as a key.</summary>
        public string ToShortString() =>
            !IsValid ? "none" : IsHost ? "host" : _value.ToString("N").Substring(0, 8);
    }
}
