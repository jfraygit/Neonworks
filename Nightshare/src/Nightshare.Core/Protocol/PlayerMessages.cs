namespace Nightshare.Core.Protocol
{
    /// <summary>
    /// Where a player is. Sent frequently and unreliably: the next one supersedes the
    /// last, so a dropped update costs nothing.
    /// <para>
    /// <see cref="Peer"/> is carried in the payload rather than inferred from the sender,
    /// because the host relays these to other clients and the recipient must know whose
    /// position it is. Relaying verbatim also keeps the host from having to rewrite
    /// packets it is only forwarding.
    /// </para>
    /// <para>
    /// Yaw only, not a full quaternion. Characters here stand upright on a navmesh, so
    /// pitch and roll carry no information and would be three quarters of the rotation
    /// budget spent on nothing.
    /// </para>
    /// </summary>
    public sealed class PlayerTransformV1
    {
        public PeerId Peer { get; set; }

        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        /// <summary>Rotation about the Y axis, in degrees.</summary>
        public float Yaw { get; set; }

        /// <summary>Drives the remote avatar's walk animation.</summary>
        public bool IsMoving { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.PlayerTransformV1);
            w.Write(Peer)
             .WriteVector3(X, Y, Z)
             .Write(Yaw)
             .Write(IsMoving);
            return w.ToArray();
        }

        public static PlayerTransformV1 Parse(NetReader r)
        {
            var peer = r.ReadPeerId();
            var (x, y, z) = r.ReadVector3();

            var m = new PlayerTransformV1
            {
                Peer = peer,
                X = x,
                Y = y,
                Z = z,
                Yaw = r.ReadSingle(),
                IsMoving = r.ReadBool(),
            };
            r.ExpectConsumed();
            return m;
        }

        public override string ToString() =>
            $"{Peer.ToShortString()} at ({X:0.0}, {Y:0.0}, {Z:0.0}) yaw {Yaw:0}{(IsMoving ? " moving" : "")}";
    }

    /// <summary>
    /// Which zone a player is in.
    /// <para>
    /// The city is 53 separate scenes rather than one world, so two players in different
    /// districts are not merely far apart: the other district is not loaded at all. A guest
    /// who loads into a zone the host is not in finds themselves sealed inside unloaded
    /// geometry with no way out.
    /// </para>
    /// <para>
    /// The scene name is used rather than a location id because it is unambiguous and
    /// always available from the active scene, whereas a location is only known after an
    /// arrival event that does not fire when a save is simply loaded.
    /// </para>
    /// </summary>
    public sealed class PlayerZoneV1
    {
        public PeerId Peer { get; set; }
        public string SceneName { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.PlayerZoneV1);
            w.Write(Peer).Write(SceneName);
            return w.ToArray();
        }

        public static PlayerZoneV1 Parse(NetReader r)
        {
            var m = new PlayerZoneV1
            {
                Peer = r.ReadPeerId(),
                SceneName = r.ReadString(),
            };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>
    /// Tells a client that a peer's avatar should exist or stop existing. Reliable, unlike
    /// the transform stream, because missing one leaves an avatar that never appears or
    /// never goes away.
    /// </summary>
    public sealed class PlayerPresenceV1
    {
        public PeerId Peer { get; set; }
        public string PlayerName { get; set; }

        /// <summary>False means the avatar should be removed.</summary>
        public bool IsPresent { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.PlayerAppearanceV1);
            w.Write(Peer).Write(PlayerName).Write(IsPresent);
            return w.ToArray();
        }

        public static PlayerPresenceV1 Parse(NetReader r)
        {
            var m = new PlayerPresenceV1
            {
                Peer = r.ReadPeerId(),
                PlayerName = r.ReadString(),
                IsPresent = r.ReadBool(),
            };
            r.ExpectConsumed();
            return m;
        }
    }
}
