namespace Nightshare.Core.Protocol
{
    /// <summary>
    /// A guest asking the host to save and send its world.
    /// <para>
    /// Sent only once the guest has a world of its own loaded, because loading the host's
    /// save means replacing it.
    /// </para>
    /// </summary>
    public sealed class SaveRequestV1
    {
        /// <summary>Name shown to the host while it saves, so it knows who is waiting.</summary>
        public string PlayerName { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.SaveRequestV1);
            w.Write(PlayerName);
            return w.ToArray();
        }

        public static SaveRequestV1 Parse(NetReader r)
        {
            var m = new SaveRequestV1 { PlayerName = r.ReadString() };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>
    /// The host's save file, for the guest to write down and load.
    /// <para>
    /// <b>This replaces assembling a world by hand.</b> A save file is the same information
    /// the old snapshot was reconstructing, written by the game's own code, and loading it
    /// puts the guest in the host's world through the path the developers test. Zone,
    /// story, NPCs and UI all arrive correct because the game does what it always does.
    /// </para>
    /// <para>
    /// Sent whole rather than chunked. A real save measured 6.5 MB against a 16 MB frame
    /// limit, and the transport already carries a 600 KB packet without complaint. If this
    /// ever needs a progress bar it will need chunking; until then one message is one less
    /// thing to reassemble.
    /// </para>
    /// </summary>
    public sealed class SaveTransferV2
    {
        /// <summary>The slot name the guest should write this to, without extension.</summary>
        public string SaveName { get; set; }

        /// <summary>Which in-game day this is, for the log and for a sanity check.</summary>
        public int GameplayGameDay { get; set; }

        /// <summary>
        /// The host's clock at capture.
        /// <para>
        /// <b>Carried for the same reason as the position: the load does not restore it.</b>
        /// A guest arrives still running whatever time it had, and the ordinary clock sync
        /// only ever nudges forward, so a guest that is <i>ahead</i> stays ahead forever. It
        /// showed up as a guest reading 20:23 against a host on 20:01, after the guest had
        /// left a paused session and kept ticking while the host stayed frozen.
        /// </para>
        /// </summary>
        public int TotalGameSeconds { get; set; }

        /// <summary>
        /// The same instant, broken into the fields the game's own time stamp uses.
        /// <para>
        /// <b>Sent as components rather than derived from the total.</b> Setting the clock
        /// absolutely needs a <c>SerializableTimeStamp</c>, and working one out from a
        /// second count means reinventing the game's own idea of which day a given total
        /// falls in. The host already has the answer; it costs four ints to ask.
        /// </para>
        /// </summary>
        public int GameDay { get; set; }
        public int Hour { get; set; }
        public int Minute { get; set; }
        public int Second { get; set; }

        public byte[] Data { get; set; }

        /// <summary>
        /// Where the host is standing, in world space, at the moment of capture.
        /// <para>
        /// <b>Carried separately even though the save already contains it.</b> Loading a
        /// save from inside a running world does not restore the player's transform: the
        /// game treats it as a zone arrival and places them at that zone's arrival point
        /// instead. Measured twice in the same zone across different sessions, landing
        /// within 2 m of each other and nowhere near the host, while a menu load of the
        /// very same file arrived correctly. So the position has to be re-applied by hand
        /// afterwards, and that means sending it.
        /// </para>
        /// </summary>
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        /// <summary>Which way the host is facing, so a guest arrives looking the same way.</summary>
        public float Yaw { get; set; }

        /// <summary>
        /// False when the host could not be located at capture. The guest then leaves the
        /// game's own placement alone rather than teleporting to the origin.
        /// </summary>
        public bool HasArrivalPoint { get; set; }

        /// <summary>
        /// The apartment the host is standing inside, or empty if they are outdoors.
        /// <para>
        /// <b>An apartment is not just a place, it is a state.</b> Its interior and exterior
        /// are two sets of objects and entering swaps which is active. A guest teleported
        /// straight to an indoor arrival point arrives inside the shell with the interior
        /// never switched on: visible host, visible door, and bare walls everywhere else.
        /// So the guest has to be told which apartment to enter, and enter it properly.
        /// </para>
        /// <para>
        /// Sent as the property's <c>Guid</c> rather than matching on position, because
        /// nearest-apartment-to-a-point is a guess and a guess that picks the neighbour puts
        /// a guest through someone else's front door.
        /// </para>
        /// </summary>
        public string ApartmentGuid { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.SaveTransferV2);
            w.Write(SaveName)
             .Write(GameplayGameDay)
             .Write(TotalGameSeconds)
             .Write(GameDay).Write(Hour).Write(Minute).Write(Second)
             .Write(HasArrivalPoint)
             .Write(X).Write(Y).Write(Z).Write(Yaw)
             .Write(ApartmentGuid ?? "")
             .WriteBytes(Data);
            return w.ToArray();
        }

        public static SaveTransferV2 Parse(NetReader r)
        {
            var m = new SaveTransferV2
            {
                SaveName = r.ReadString(),
                GameplayGameDay = r.ReadInt32(),
                TotalGameSeconds = r.ReadInt32(),
                GameDay = r.ReadInt32(),
                Hour = r.ReadInt32(),
                Minute = r.ReadInt32(),
                Second = r.ReadInt32(),
                HasArrivalPoint = r.ReadBool(),
                X = r.ReadSingle(),
                Y = r.ReadSingle(),
                Z = r.ReadSingle(),
                Yaw = r.ReadSingle(),
                ApartmentGuid = r.ReadString(),
                Data = r.ReadBytes(),
            };
            r.ExpectConsumed();
            return m;
        }
    }
}
