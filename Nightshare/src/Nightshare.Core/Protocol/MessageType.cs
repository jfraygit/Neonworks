namespace Nightshare.Core.Protocol
{
    /// <summary>
    /// Every message on the wire.
    /// <para>
    /// <b>The version suffix is part of the contract.</b> When a message's payload layout
    /// changes, do not edit it in place: add the next version as a new member and retire
    /// the old one. An old peer then has no handler for the new type and ignores it, so
    /// the feature is dead until both sides update. Editing in place instead means an old
    /// peer reads new bytes into an old shape, passes every validity check, and acts on
    /// garbage. Dead beats wrong.
    /// </para>
    /// <para>
    /// Numbers are explicit and must never be reused, including by a retired message.
    /// Blocks are grouped so a category has room to grow.
    /// </para>
    /// </summary>
    public enum MessageType : ushort
    {
        None = 0,

        // --- handshake and session, 1 to 19 ---
        HelloV1 = 1,
        WelcomeV1 = 2,
        RejectV1 = 3,
        PeerJoinedV1 = 4,
        PeerLeftV1 = 5,
        PingV1 = 6,
        PongV1 = 7,
        DisconnectV1 = 8,

        /// <summary>
        /// A host announcing itself on the local network, over UDP rather than the session's
        /// TCP connection. Sent to nobody in particular, so a guest can find a session
        /// without being told an address.
        /// </summary>
        LobbyBeaconV1 = 9,

        // --- world state, 20 to 39 ---

        // 20, 21 and 22 were WorldSnapshotRequestV1, WorldSnapshotV1 and ManagerPacketV1:
        // the world sent as one message per manager, for a client to apply to its running
        // game. Replaced by sending the save file itself. RETIRED, AND THE NUMBERS STAY
        // SPENT. Reusing one means an old peer reads a new message into an old shape,
        // passes every validity check, and acts on nonsense.

        /// <summary>A guest asking the host to save and send its world.</summary>
        SaveRequestV1 = 23,

        /// <summary>Retired. Carried no arrival point, so a guest landed wherever the
        /// game's zone arrival put them rather than beside the host.</summary>
        SaveTransferV1 = 24,

        /// <summary>The host's save file, plus where the host is standing in it.</summary>
        SaveTransferV2 = 25,

        // --- clock, 40 to 59 ---
        ClockSyncV1 = 40,
        SleepRequestV1 = 41,
        SleepStateV1 = 42,
        DayAdvancedV1 = 43,

        /// <summary>Everyone is at a bed; lay down now, together.</summary>
        SleepProceedV1 = 44,

        // --- player replication, 60 to 89 ---
        PlayerTransformV1 = 60,

        /// <summary>Which zone a player is in. The city is 53 separate scenes.</summary>
        PlayerZoneV1 = 63,
        PlayerAppearanceV1 = 61,
        PlayerActionV1 = 62,

        // --- diagnostics, 200 and up ---
        StateHashV1 = 200,
        DesyncReportV1 = 201,
        DebugLogV1 = 202,
    }

    /// <summary>Why a join was refused. Sent in <see cref="MessageType.RejectV1"/>.</summary>
    public enum RejectReason : byte
    {
        Unknown = 0,
        ProtocolMismatch = 1,
        ModVersionMismatch = 2,
        GameVersionMismatch = 3,
        SessionFull = 4,
        SessionClosed = 5,
        MalformedHandshake = 6,
    }
}
