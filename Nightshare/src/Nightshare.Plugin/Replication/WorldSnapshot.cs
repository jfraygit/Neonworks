using System;
using System.Collections.Generic;
using Nivalis;

namespace Nightshare.Replication
{
    /// <summary>One manager's serialised state, ready for the wire.</summary>
    internal sealed class SnapshotEntry
    {
        public string ManagerTypeName;
        public string PacketGuid;
        public byte[] Payload;
    }

    /// <summary>
    /// Builds the host's world state for a joining player.
    /// <para>
    /// Only world-owned managers are included. A guest keeps their own character,
    /// progression and narrative state, so sending those would overwrite the player's own
    /// with the host's.
    /// </para>
    /// </summary>
    internal static class WorldSnapshot
    {
        /// <summary>
        /// The host's own pockets. A constant key, so host and guest both want the same
        /// dictionary entry and one would silently overwrite the other. Stripped before
        /// sending. See docs/recon.md.
        /// </summary>
        private const string PlayerInventoryId = "PLAYER_INVENTORY";

        public static List<SnapshotEntry> Build(Action<string> log)
        {
            var entries = new List<SnapshotEntry>();
            var skipped = 0;
            var failed = 0;
            var totalBytes = 0;

            List<IManager> managers;
            try
            {
                managers = SaveSerialiser.FindManagers();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Snapshot: could not enumerate managers: {ex.Message}");
                return entries;
            }

            foreach (var manager in managers)
            {
                if (manager == null) continue;

                var typeName = SaveSerialiser.ManagerNameOf(manager);
                if (!ManagerOwnership.IsWorldOwned(typeName)) { skipped++; continue; }

                ISavePacket packet;
                try
                {
                    packet = manager.CreatePacket();
                    if (packet == null) continue;       // no state to send
                    manager.WriteToPacket(packet);
                }
                catch (Exception ex)
                {
                    failed++;
                    log?.Invoke($"Snapshot: {typeName} would not produce a packet: {ex.Message}");
                    continue;
                }

                // Strip anything that belongs to the host personally before it goes out.
                try { Redact(typeName, packet, log); }
                catch (Exception ex)
                {
                    // Failing to redact is worse than failing to send: it would leak the
                    // host's own state into the guest's world. Drop the packet instead.
                    failed++;
                    log?.Invoke($"Snapshot: {typeName} could not be redacted ({ex.Message}), omitted");
                    continue;
                }

                var bytes = SaveSerialiser.TrySerialise(packet, out var error);
                if (bytes == null)
                {
                    failed++;
                    log?.Invoke($"Snapshot: {typeName} failed to serialise: {error}");
                    continue;
                }

                string guid;
                try { guid = packet.Guid; } catch (Exception) { guid = typeName; }

                entries.Add(new SnapshotEntry
                {
                    ManagerTypeName = typeName,
                    PacketGuid = guid,
                    Payload = bytes,
                });

                totalBytes += bytes.Length;
            }

            log?.Invoke($"Snapshot: {entries.Count} packet(s), {totalBytes:N0} bytes " +
                        $"({totalBytes / 1024:N0} KB), {skipped} not world-owned" +
                        (failed > 0 ? $", {failed} FAILED" : ""));

            return entries;
        }

        /// <summary>
        /// Remove anything host-personal from a world packet before it is sent.
        /// </summary>
        private static void Redact(string typeName, ISavePacket packet, Action<string> log)
        {
            if (typeName != "InventoriesManager") return;

            var inventories = packet.TryCast<Nivalis.InventorySystem.InventoriesSave>();
            if (inventories == null)
            {
                log?.Invoke("Snapshot: InventoriesManager packet was not an InventoriesSave, " +
                            "so PLAYER_INVENTORY could not be stripped");
                return;
            }

            var list = inventories.Inventories;
            if (list == null) return;

            // Backwards, because removing shifts everything after the index.
            var removed = 0;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                string id;
                try { id = list[i].Item1; } catch (Exception) { continue; }

                if (string.Equals(id, PlayerInventoryId, StringComparison.Ordinal))
                {
                    list.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0)
                log?.Invoke($"Snapshot: stripped the host's own {PlayerInventoryId} " +
                            $"({removed} entr{(removed == 1 ? "y" : "ies")})");
        }
    }
}
