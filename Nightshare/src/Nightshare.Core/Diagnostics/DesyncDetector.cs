using System;
using System.Collections.Generic;
using System.Text;

namespace Nightshare.Core.Diagnostics
{
    /// <summary>What a peer reported for one manager at one tick.</summary>
    public readonly struct ManagerFingerprint
    {
        public ManagerFingerprint(string managerTypeName, ulong hash, int payloadBytes)
        {
            ManagerTypeName = managerTypeName;
            Hash = hash;
            PayloadBytes = payloadBytes;
        }

        public string ManagerTypeName { get; }
        public ulong Hash { get; }
        public int PayloadBytes { get; }
    }

    /// <summary>A manager whose state differs between two peers.</summary>
    public sealed class Divergence
    {
        public int Tick { get; init; }
        public string ManagerTypeName { get; init; }
        public PeerId PeerA { get; init; }
        public PeerId PeerB { get; init; }
        public ulong HashA { get; init; }
        public ulong HashB { get; init; }
        public int BytesA { get; init; }
        public int BytesB { get; init; }

        /// <summary>True the first time this manager diverged. Later ticks are aftershocks.</summary>
        public bool IsFirstOccurrence { get; init; }

        public override string ToString()
        {
            var size = BytesA != BytesB ? $"  size {BytesA} vs {BytesB}" : "";
            return $"tick {Tick}  {ManagerTypeName}  " +
                   $"{PeerA.ToShortString()}={StateHash.Format(HashA)} " +
                   $"{PeerB.ToShortString()}={StateHash.Format(HashB)}{size}  DIVERGED";
        }
    }

    /// <summary>
    /// Compares per-manager state fingerprints across peers and reports the first manager
    /// to diverge.
    /// <para>
    /// This is the single most valuable tool in the harness. Without it a desync presents
    /// as "the world feels wrong an hour in" and the search space is every system at once.
    /// With it, the report names the manager and the tick, which usually identifies the
    /// bug outright.
    /// </para>
    /// <para>
    /// <b>Only the first divergence per manager is worth acting on.</b> Once a manager is
    /// wrong it stays wrong and reports every tick, and a diverged manager frequently drags
    /// others with it. Chasing the tenth report is chasing a symptom.
    /// </para>
    /// </summary>
    public sealed class DesyncDetector
    {
        private readonly Dictionary<int, Dictionary<PeerId, Dictionary<string, ManagerFingerprint>>> _byTick = new();
        private readonly HashSet<string> _alreadyDiverged = new();
        private readonly List<Divergence> _divergences = new();

        /// <summary>
        /// Ticks retained before being discarded. Peers do not report in lockstep, so a
        /// tick must survive long enough for everyone's report to arrive.
        /// </summary>
        public int TickWindow { get; set; } = 64;

        /// <summary>Every divergence seen, newest last.</summary>
        public IReadOnlyList<Divergence> Divergences => _divergences;

        /// <summary>Managers that have diverged at least once.</summary>
        public IReadOnlyCollection<string> DivergedManagers => _alreadyDiverged;

        public bool HasDiverged => _divergences.Count > 0;

        /// <summary>Raised once per divergence. Wire to the log.</summary>
        public event Action<Divergence> Diverged;

        /// <summary>
        /// Record what a peer reported, and compare against everyone else at that tick.
        /// Returns any divergences this report revealed.
        /// </summary>
        public IReadOnlyList<Divergence> Report(PeerId peer, int tick, ManagerFingerprint fingerprint)
        {
            if (!_byTick.TryGetValue(tick, out var peers))
            {
                peers = new Dictionary<PeerId, Dictionary<string, ManagerFingerprint>>();
                _byTick[tick] = peers;
            }

            if (!peers.TryGetValue(peer, out var managers))
            {
                managers = new Dictionary<string, ManagerFingerprint>();
                peers[peer] = managers;
            }

            managers[fingerprint.ManagerTypeName] = fingerprint;

            var found = Compare(tick, fingerprint.ManagerTypeName);
            Prune(tick);
            return found;
        }

        private List<Divergence> Compare(int tick, string managerTypeName)
        {
            var found = new List<Divergence>();
            var peers = _byTick[tick];
            if (peers.Count < 2) return found;

            // Compare every peer reporting this manager against the first one that did.
            PeerId baseline = default;
            ManagerFingerprint baselinePrint = default;
            var haveBaseline = false;

            foreach (var kv in peers)
            {
                if (!kv.Value.TryGetValue(managerTypeName, out var print)) continue;

                if (!haveBaseline)
                {
                    baseline = kv.Key;
                    baselinePrint = print;
                    haveBaseline = true;
                    continue;
                }

                if (print.Hash == baselinePrint.Hash) continue;

                var first = _alreadyDiverged.Add(managerTypeName);
                var divergence = new Divergence
                {
                    Tick = tick,
                    ManagerTypeName = managerTypeName,
                    PeerA = baseline,
                    PeerB = kv.Key,
                    HashA = baselinePrint.Hash,
                    HashB = print.Hash,
                    BytesA = baselinePrint.PayloadBytes,
                    BytesB = print.PayloadBytes,
                    IsFirstOccurrence = first,
                };

                _divergences.Add(divergence);
                found.Add(divergence);
                Diverged?.Invoke(divergence);
            }

            return found;
        }

        private void Prune(int currentTick)
        {
            if (_byTick.Count <= TickWindow) return;

            var cutoff = currentTick - TickWindow;
            List<int> stale = null;
            foreach (var t in _byTick.Keys)
                if (t < cutoff) (stale ??= new List<int>()).Add(t);

            if (stale == null) return;
            foreach (var t in stale) _byTick.Remove(t);
        }

        /// <summary>
        /// Human-readable summary. Leads with the first divergence, which is the one that
        /// is actually worth investigating.
        /// </summary>
        public string BuildReport()
        {
            if (_divergences.Count == 0) return "No divergence detected.";

            var sb = new StringBuilder();
            var firsts = new List<Divergence>();
            foreach (var d in _divergences)
                if (d.IsFirstOccurrence) firsts.Add(d);

            sb.AppendLine($"DESYNC: {firsts.Count} manager(s) diverged, {_divergences.Count} report(s) total.");
            sb.AppendLine();
            sb.AppendLine("First divergence per manager, earliest first. Investigate the top entry:");

            firsts.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            foreach (var d in firsts) sb.AppendLine("  " + d);

            if (firsts.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine("Later entries are frequently knock-on effects of the first. " +
                              "Fix the earliest one and re-run before reading the rest.");
            }

            return sb.ToString();
        }

        public void Reset()
        {
            _byTick.Clear();
            _alreadyDiverged.Clear();
            _divergences.Clear();
        }
    }
}
