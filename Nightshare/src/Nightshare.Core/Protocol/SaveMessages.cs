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
    public sealed class SaveTransferV1
    {
        /// <summary>The slot name the guest should write this to, without extension.</summary>
        public string SaveName { get; set; }

        /// <summary>Which in-game day this is, for the log and for a sanity check.</summary>
        public int GameplayGameDay { get; set; }

        public byte[] Data { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.SaveTransferV1);
            w.Write(SaveName).Write(GameplayGameDay).WriteBytes(Data);
            return w.ToArray();
        }

        public static SaveTransferV1 Parse(NetReader r)
        {
            var m = new SaveTransferV1
            {
                SaveName = r.ReadString(),
                GameplayGameDay = r.ReadInt32(),
                Data = r.ReadBytes(),
            };
            r.ExpectConsumed();
            return m;
        }
    }
}
