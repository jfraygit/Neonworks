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

    /// <summary>
    /// One manager's save packet, verbatim from the game's own <c>ISaveWriter</c>.
    /// <see cref="PacketGuid"/> is the game's <c>ISavePacket.Guid</c>.
    /// </summary>
    /// <remarks>
    /// Packet ids are opaque strings, not GUIDs. Several of the game's own are not
    /// parseable as a <c>System.Guid</c> (<c>Guid_PLAYER_MANAGER_SAVE</c>, <c>AI-Ai-ai</c>
    /// and others with non-hex characters). Never call Guid.Parse on one.
    /// </remarks>
    public sealed class ManagerPacketV1
    {
        public string PacketGuid { get; set; }
        public string ManagerTypeName { get; set; }
        public byte[] Payload { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.ManagerPacketV1);
            w.Write(PacketGuid).Write(ManagerTypeName).WriteBytes(Payload);
            return w.ToArray();
        }

        public static ManagerPacketV1 Parse(NetReader r)
        {
            var m = new ManagerPacketV1
            {
                PacketGuid = r.ReadString(),
                ManagerTypeName = r.ReadString(),
                Payload = r.ReadBytes(),
            };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>
    /// A client telling the host it is ready to receive the world.
    /// <para>
    /// <b>The snapshot cannot be sent at handshake time.</b> A client connects from the
    /// main menu, long before it loads a save, and at that point none of its managers
    /// exist: a snapshot sent then finds nothing to apply to and is silently discarded.
    /// Measured, 16 of 17 packets skipped with "no such manager here". The client asks
    /// only once its own world is up.
    /// </para>
    /// </summary>
    public sealed class WorldSnapshotRequestV1
    {
        /// <summary>How many managers the client can see, for the host's log.</summary>
        public int LocalManagerCount { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.WorldSnapshotRequestV1);
            w.Write(LocalManagerCount);
            return w.ToArray();
        }

        public static WorldSnapshotRequestV1 Parse(NetReader r)
        {
            var m = new WorldSnapshotRequestV1 { LocalManagerCount = r.ReadInt32() };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>
    /// Announces a world snapshot. The <see cref="PacketCount"/> ManagerPacketV1 messages
    /// that follow make up the snapshot, in order.
    /// <para>
    /// Sent as a header rather than one giant message so the receiver can report progress
    /// and can tell "still arriving" from "the host stopped sending". A snapshot is over a
    /// megabyte and takes visible time.
    /// </para>
    /// </summary>
    public sealed class WorldSnapshotV1
    {
        public int PacketCount { get; set; }
        public int TotalBytes { get; set; }

        /// <summary>Host clock at the moment the snapshot was taken.</summary>
        public int TotalGameSeconds { get; set; }
        public int GameplayGameDay { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.WorldSnapshotV1);
            w.Write(PacketCount).Write(TotalBytes)
             .Write(TotalGameSeconds).Write(GameplayGameDay);
            return w.ToArray();
        }

        public static WorldSnapshotV1 Parse(NetReader r)
        {
            var m = new WorldSnapshotV1
            {
                PacketCount = r.ReadInt32(),
                TotalBytes = r.ReadInt32(),
                TotalGameSeconds = r.ReadInt32(),
                GameplayGameDay = r.ReadInt32(),
            };
            r.ExpectConsumed();
            return m;
        }
    }

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
