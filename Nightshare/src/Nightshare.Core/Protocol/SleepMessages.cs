namespace Nightshare.Core.Protocol
{
    /// <summary>
    /// A player saying they want to sleep, or that they have changed their mind.
    /// <para>
    /// Sleeping is the one action that moves the world for everybody, so no single player
    /// gets to do it alone. This is the request; the host decides when it happens.
    /// </para>
    /// </summary>
    public sealed class SleepRequestV1
    {
        /// <summary>False when a player gets out of bed before everyone is ready.</summary>
        public bool WantsToSleep { get; set; }

        /// <summary>Total game seconds they want to wake at, from the game's own sleep call.</summary>
        public int TargetGameSeconds { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.SleepRequestV1);
            w.Write(WantsToSleep).Write(TargetGameSeconds);
            return w.ToArray();
        }

        public static SleepRequestV1 Parse(NetReader r)
        {
            var m = new SleepRequestV1
            {
                WantsToSleep = r.ReadBool(),
                TargetGameSeconds = r.ReadInt32(),
            };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>
    /// Who is in bed and who the session is still waiting for. Broadcast whenever it
    /// changes, so every player can be shown the same thing.
    /// </summary>
    public sealed class SleepStateV1
    {
        public int ReadyCount { get; set; }
        public int TotalCount { get; set; }

        /// <summary>Names of the players not yet in bed, comma separated, for the prompt.</summary>
        public string WaitingOn { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.SleepStateV1);
            w.Write(ReadyCount).Write(TotalCount).Write(WaitingOn);
            return w.ToArray();
        }

        public static SleepStateV1 Parse(NetReader r)
        {
            var m = new SleepStateV1
            {
                ReadyCount = r.ReadInt32(),
                TotalCount = r.ReadInt32(),
                WaitingOn = r.ReadString(),
            };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>
    /// Everybody is at a bed. Lay down now.
    /// <para>
    /// The sleep sequence is a long one: an animation, a day summary, shop rankings, a
    /// newspaper. Each player runs their own locally so they all see it, which is why this
    /// is a "go" signal rather than the host simply doing it and telling the others the
    /// result.
    /// </para>
    /// </summary>
    public sealed class SleepProceedV1
    {
        /// <summary>Total game seconds everybody should wake at.</summary>
        public int TargetGameSeconds { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.SleepProceedV1);
            w.Write(TargetGameSeconds);
            return w.ToArray();
        }

        public static SleepProceedV1 Parse(NetReader r)
        {
            var m = new SleepProceedV1 { TargetGameSeconds = r.ReadInt32() };
            r.ExpectConsumed();
            return m;
        }
    }

    /// <summary>
    /// The host has slept and the world has moved on. Clients apply the new time.
    /// <para>
    /// Separate from the ordinary clock broadcast because a day advance is a jump rather
    /// than a tick, and the clock replicator refuses to move time backwards or by a large
    /// step without being told that is what is meant.
    /// </para>
    /// </summary>
    public sealed class DayAdvancedV1
    {
        public int TotalGameSeconds { get; set; }
        public int GameplayGameDay { get; set; }

        public byte[] Serialise()
        {
            using var w = new NetWriter(MessageType.DayAdvancedV1);
            w.Write(TotalGameSeconds).Write(GameplayGameDay);
            return w.ToArray();
        }

        public static DayAdvancedV1 Parse(NetReader r)
        {
            var m = new DayAdvancedV1
            {
                TotalGameSeconds = r.ReadInt32(),
                GameplayGameDay = r.ReadInt32(),
            };
            r.ExpectConsumed();
            return m;
        }
    }
}
