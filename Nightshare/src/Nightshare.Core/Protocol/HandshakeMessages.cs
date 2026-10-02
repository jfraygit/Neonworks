namespace Nightshare.Core.Protocol
{
    /// <summary>
    /// First thing a joining client sends. Carries everything the host needs to decide
    /// whether the two builds can safely play together.
    /// </summary>
    public sealed class HelloV1
    {
        public int ProtocolVersion { get; set; }
        public string ModVersion { get; set; }
        public string GameBuildId { get; set; }
        public string PlayerName { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.HelloV1);
            w.Write(ProtocolVersion)
             .Write(ModVersion)
             .Write(GameBuildId)
             .Write(PlayerName);
            return w.ToArray();
        }

        public static HelloV1 Parse(NetReader r)
        {
            var m = new HelloV1
            {
                ProtocolVersion = r.ReadInt32(),
                ModVersion = r.ReadString(),
                GameBuildId = r.ReadString(),
                PlayerName = r.ReadString(),
            };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>Host accepts the join and tells the client who it is.</summary>
    public sealed class WelcomeV1
    {
        /// <summary>The id the host has assigned. The client adopts this as its own.</summary>
        public PeerId AssignedPeerId { get; set; }

        public string HostPlayerName { get; set; }

        /// <summary>Identifies this world across sessions, so a returning guest can be recognised.</summary>
        public string SessionId { get; set; }

        /// <summary>Host clock at the moment of acceptance, in whole in-game seconds.</summary>
        public int TotalGameSeconds { get; set; }

        public int GameplayGameDay { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.WelcomeV1);
            w.Write(AssignedPeerId)
             .Write(HostPlayerName)
             .Write(SessionId)
             .Write(TotalGameSeconds)
             .Write(GameplayGameDay);
            return w.ToArray();
        }

        public static WelcomeV1 Parse(NetReader r)
        {
            var m = new WelcomeV1
            {
                AssignedPeerId = r.ReadPeerId(),
                HostPlayerName = r.ReadString(),
                SessionId = r.ReadString(),
                TotalGameSeconds = r.ReadInt32(),
                GameplayGameDay = r.ReadInt32(),
            };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>
    /// Host refuses the join. <see cref="Detail"/> is shown to the player verbatim, so it
    /// must say what to actually do about it rather than just naming the fault.
    /// </summary>
    public sealed class RejectV1
    {
        public RejectReason Reason { get; set; }
        public string Detail { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.RejectV1);
            w.Write((byte)Reason).Write(Detail);
            return w.ToArray();
        }

        public static RejectV1 Parse(NetReader r)
        {
            var m = new RejectV1
            {
                Reason = (RejectReason)r.ReadByte(),
                Detail = r.ReadString(),
            };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>Host clock broadcast. Clients hold no authority over time.</summary>
    public sealed class ClockSyncV1
    {
        public int TotalGameSeconds { get; set; }
        public int GameplayGameDay { get; set; }
        public int ClockHour { get; set; }
        public int ClockMinute { get; set; }
        public bool IsPaused { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.ClockSyncV1);
            w.Write(TotalGameSeconds)
             .Write(GameplayGameDay)
             .Write(ClockHour)
             .Write(ClockMinute)
             .Write(IsPaused);
            return w.ToArray();
        }

        public static ClockSyncV1 Parse(NetReader r)
        {
            var m = new ClockSyncV1
            {
                TotalGameSeconds = r.ReadInt32(),
                GameplayGameDay = r.ReadInt32(),
                ClockHour = r.ReadInt32(),
                ClockMinute = r.ReadInt32(),
                IsPaused = r.ReadBool(),
            };
            r.ExpectConsumed();
            return m;
        }
    }

    // ManagerPacketV1, WorldSnapshotRequestV1 and WorldSnapshotV1 lived here.
    //
    // They carried the world as one message per manager, for a client to apply to its
    // already running game. That approach is gone. A guest is sent the host's SAVE FILE
    // and the game loads it, which is the path the developers test and the only one that
    // brings the story, the people and the UI up agreeing with each other.
    //
    // Their numbers, 20 to 22, stay spent. See MessageType.

    /// <summary>
    /// Periodic state fingerprint for the desync detector. Peers exchange these and
    /// compare; a mismatch names the manager that diverged first.
    /// </summary>
    public sealed class StateHashV1
    {
        public int Tick { get; set; }
        public string ManagerTypeName { get; set; }
        public ulong Hash { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.StateHashV1);
            w.Write(Tick).Write(ManagerTypeName).Write(Hash);
            return w.ToArray();
        }

        public static StateHashV1 Parse(NetReader r)
        {
            var m = new StateHashV1
            {
                Tick = r.ReadInt32(),
                ManagerTypeName = r.ReadString(),
                Hash = r.ReadUInt64(),
            };
            r.ExpectConsumed();
            return m;
        }
    }
}
