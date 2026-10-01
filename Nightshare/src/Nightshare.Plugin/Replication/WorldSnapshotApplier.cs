using System;
using System.Collections.Generic;
using Nightshare.Core.Protocol;
using Nivalis;

namespace Nightshare.Replication
{
    /// <summary>
    /// Applies a received world snapshot to the local game.
    /// <para>
    /// <b>This is the most invasive thing the mod does.</b> It drives the game's own load
    /// path, <c>IManager.InitializeInternal</c>, on a world that is already running. A
    /// fault here does not produce a network error; it produces a broken city.
    /// </para>
    /// <para>
    /// Therefore: it is off by default, it refuses rather than guesses when anything is
    /// missing, and it reports every manager it touches. Turn it on with
    /// <c>ApplyWorldSnapshot</c> in the config, and take a backup save first.
    /// </para>
    /// </summary>
    internal static class WorldSnapshotApplier
    {
        /// <summary>
        /// Apply a complete snapshot. Returns the number of managers updated.
        /// </summary>
        public static int Apply(IReadOnlyList<ManagerPacketV1> packets, Action<string> log)
        {
            if (packets == null || packets.Count == 0) return 0;

            var bootstrap = FindBootstrap(log);
            if (bootstrap == null)
            {
                log("Apply: no ManagersBootstrap, refusing. Nothing was changed.");
                return 0;
            }

            var configs = ReadConfigurationMap(bootstrap, log);
            if (configs == null)
            {
                log("Apply: could not read the manager configuration map, refusing. " +
                    "Nothing was changed.");
                return 0;
            }

            // Index the live managers by type name so each packet can find its owner.
            var byName = new Dictionary<string, IManager>(StringComparer.Ordinal);
            foreach (var manager in SaveSerialiser.FindManagers())
            {
                if (manager == null) continue;
                byName[SaveSerialiser.ManagerNameOf(manager)] = manager;
            }

            var applied = 0;
            var skipped = 0;

            // Managers that took data, so phase two can be run over exactly those.
            var touched = new List<IManager>();

            // Tell the game a load is in progress. Managers behave differently during one,
            // and applying a world without this is asking systems to react to a hundred
            // simultaneous changes as though the player had caused each of them.
            var previousLoading = false;
            try
            {
                previousLoading = bootstrap.IsLoadingSaveFile;
                bootstrap.IsLoadingSaveFile = true;
            }
            catch (Exception ex)
            {
                log($"Apply: could not set IsLoadingSaveFile ({ex.Message}), continuing anyway");
            }

            try
            {
                foreach (var p in packets)
                {
                    if (p == null) continue;

                    if (!byName.TryGetValue(p.ManagerTypeName, out var manager) || manager == null)
                    {
                        skipped++;
                        log($"    {p.ManagerTypeName}: no such manager here, skipped");
                        continue;
                    }

                    // Never apply something the ownership rules say is not the world's.
                    // A mislabelled packet would overwrite the player's own state.
                    if (!ManagerOwnership.IsWorldOwned(p.ManagerTypeName))
                    {
                        skipped++;
                        log($"    {p.ManagerTypeName}: not world-owned, REFUSED");
                        continue;
                    }

                    ISavePacket packet;
                    try
                    {
                        packet = manager.CreatePacket();
                        if (packet == null) { skipped++; continue; }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        log($"    {p.ManagerTypeName}: CreatePacket threw {ex.GetType().Name}, skipped");
                        continue;
                    }

                    if (!SaveSerialiser.TryDeserialise(packet, p.Payload, out var error))
                    {
                        skipped++;
                        log($"    {p.ManagerTypeName}: could not read the packet ({error}), skipped");
                        continue;
                    }

                    ManagerConfiguration config = null;
                    try
                    {
                        var type = GetConfigurationType(manager);
                        if (type != null) configs.TryGetValue(type, out config);
                    }
                    catch (Exception) { }

                    try
                    {
                        manager.InitializeInternal(config, packet);
                        touched.Add(manager);
                        applied++;
                        log($"    {p.ManagerTypeName,-46} applied {p.Payload?.Length ?? 0,9:N0} b");
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        log($"    {p.ManagerTypeName}: InitializeInternal threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            finally
            {
                try { bootstrap.IsLoadingSaveFile = previousLoading; }
                catch (Exception) { }
            }

            // PHASE TWO.
            //
            // IManager is a two-phase init: InitializeInternal loads the data, then
            // InitializeExternal wires it up and tells everything the state has changed.
            // Running only the first phase leaves every manager holding correct data that
            // nothing has been told about, so the game looks unchanged. Measured: a quest
            // picked up on the host transferred and applied perfectly, 2,743 bytes, and the
            // guest's quest log still showed the old list.
            //
            // The game loads every manager before wiring any of them up, so the two passes
            // are kept separate here too: a manager waking up mid-pass must not see a
            // half-applied world.
            var wired = 0;
            foreach (var manager in touched)
            {
                try
                {
                    manager.InitializeExternal();
                    wired++;
                }
                catch (Exception ex)
                {
                    log($"    {SaveSerialiser.ManagerNameOf(manager)}: InitializeExternal threw " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            RefreshUi(log);

            log($"Apply: {applied} manager(s) updated, {wired} wired up, {skipped} skipped");
            return applied;
        }

        /// <summary>
        /// Tell the UI its data moved under it.
        /// <para>
        /// A backstop behind <c>InitializeExternal</c>. Panels built at load time cache
        /// what they were given and rebuild only when told, so a quest log can hold a
        /// perfectly stale list beside a manager full of correct data. These are the
        /// game's own "it changed" events, raised with nothing else attached.
        /// </para>
        /// </summary>
        private static void RefreshUi(Action<string> log)
        {
            try
            {
                var quests = UnityEngine.Object.FindObjectOfType<QuestManager>();
                if (quests != null)
                {
                    quests.OnQuestsUpdated?.Invoke();
                    quests.OnQuestsPinnedChanged?.Invoke();
                    log("    refreshed the quest log");
                }
            }
            catch (Exception ex)
            {
                log($"    quest log refresh failed: {ex.Message}");
            }
        }

        private static ManagersBootstrap FindBootstrap(Action<string> log)
        {
            try
            {
                return UnityEngine.Object.FindObjectOfType<ManagersBootstrap>();
            }
            catch (Exception ex)
            {
                log($"Apply: could not find ManagersBootstrap: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get the map from manager type to its configuration.
        /// <para>
        /// The live map is a private field. Il2CppInterop generates private fields as
        /// accessible, so it is read directly, with the public OverrideConfiguration as a
        /// fallback for when it is not.
        /// </para>
        /// </summary>
        private static Il2CppSystem.Collections.Generic.IReadOnlyDictionary<Il2CppSystem.Type, ManagerConfiguration>
            ReadConfigurationMap(ManagersBootstrap bootstrap, Action<string> log)
        {
            try
            {
                var map = bootstrap.configurationMap;
                if (map != null) return map;
            }
            catch (Exception ex)
            {
                log($"Apply: configurationMap unreadable ({ex.Message}), trying OverrideConfiguration");
            }

            try
            {
                var over = bootstrap.OverrideConfiguration;
                if (over != null) return over.ManagerConfigurations;
            }
            catch (Exception) { }

            return null;
        }

        private static Il2CppSystem.Type GetConfigurationType(IManager manager)
        {
            try { return manager.ConfigurationType; }
            catch (Exception) { return null; }
        }
    }
}
