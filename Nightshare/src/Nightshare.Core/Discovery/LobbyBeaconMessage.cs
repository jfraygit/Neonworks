using Nightshare.Core.Protocol;

namespace Nightshare.Core.Discovery
{
    /// <summary>
    /// What a host shouts onto the local network so guests can find it.
    /// <para>
    /// <b>Everything needed to decide whether joining will work is in here.</b> A browser
    /// that lists a session and then fails on connect because the builds differ is worse
    /// than one that shows it greyed out with the reason, so the beacon carries the versions
    /// rather than making the guest connect to find out.
    /// </para>
    /// <para>
    /// The address is deliberately NOT in the beacon. It is taken from the UDP packet's
    /// sender, which is the one piece of routing information that cannot be wrong: a host
    /// behind a router does not know its own reachable address, and a host that guessed
    /// would advertise one nobody can reach.
    /// </para>
    /// </summary>
    public sealed class LobbyBeaconV1
    {
        /// <summary>Who is hosting, as the guest will see it in the list.</summary>
        public string HostName { get; set; } = "";

        /// <summary>The TCP port to actually connect to. The UDP port is a fixed one.</summary>
        public int Port { get; set; }

        /// <summary>Wire protocol version, so a mismatch is visible before connecting.</summary>
        public int ProtocolVersion { get; set; }

        /// <summary>Nightshare's version, for the same reason.</summary>
        public string ModVersion { get; set; } = "";

        /// <summary>The game build fingerprint. Different builds must never connect.</summary>
        public string GameBuildId { get; set; } = "";

        /// <summary>How many people are already in, for the list.</summary>
        public int PlayerCount { get; set; }

        /// <summary>
        /// Identifies the session rather than the machine, so a host that stops and restarts
        /// is not confused with the one that was there a moment ago.
        /// </summary>
        public string SessionId { get; set; } = "";

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.LobbyBeaconV1);
            w.Write(HostName ?? "")
             .Write(Port)
             .Write(ProtocolVersion)
             .Write(ModVersion ?? "")
             .Write(GameBuildId ?? "")
             .Write(PlayerCount)
             .Write(SessionId ?? "");
            return w.ToArray();
        }

        public static LobbyBeaconV1 Parse(NetReader r)
        {
            var m = new LobbyBeaconV1
            {
                HostName = r.ReadString(),
                Port = r.ReadInt32(),
                ProtocolVersion = r.ReadInt32(),
                ModVersion = r.ReadString(),
                GameBuildId = r.ReadString(),
                PlayerCount = r.ReadInt32(),
                SessionId = r.ReadString(),
            };
            r.ExpectConsumed();
            return m;
        }
    }
}
