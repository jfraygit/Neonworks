using System;
using HarmonyLib;

namespace Nightshare.Replication
{
    /// <summary>
    /// Releases the pause the game takes while loading, when it forgets to.
    /// <para>
    /// <b>The bug this exists for.</b> Loading a save pauses time, which is correct. In the
    /// game's own flow that pause is released when the load finishes. Driving a load from
    /// inside a running world, which is what a guest joining does, leaves it held: the
    /// loading screen drops, the player can walk around, and the clock never moves again.
    /// </para>
    /// <para>
    /// It took a while to find because every obvious suspect was innocent. Nightshare's own
    /// pause lock was being taken and released correctly, and the log said so. The thing
    /// that actually named it was the game's own <c>OverrideableBool.LogOwners()</c>:
    /// <code>
    /// Lock owners (1): ------ Managers (Nivalis.SerializationManager)
    /// </code>
    /// A pause is reference counted across owners, so releasing ours resumed nothing while
    /// the serialiser still held one. <b>Releasing your own lock is not the same as the
    /// world running again</b>, and only asking the game who else is holding it can tell
    /// the difference.
    /// </para>
    /// <para>
    /// <b>Why releasing someone else's lock is defensible here.</b> Only once the loading
    /// screen is down and the local player exists and the world has settled. At that point
    /// a load-time pause is stale by definition: the load it belonged to is over. The
    /// alternative is a guest sitting in a city where time never passes.
    /// </para>
    /// </summary>
    internal static class LoadPauseGuard
    {
        private static Nivalis.OverrideableBool.OverrideLock _serialiserLock;
        private static bool _warnedNotTracked;

        /// <summary>
        /// Remember a lock taken against the time-of-day pause.
        /// <para>
        /// Filtered hard: <c>Override</c> is how every gate in the game is held, including
        /// player movement and menu shortcuts, so this runs on a hot path and must do as
        /// little as possible for the calls it does not care about.
        /// </para>
        /// </summary>
        public static void Record(Nivalis.OverrideableBool target,
                                  Il2CppSystem.Object owner,
                                  Nivalis.OverrideableBool.OverrideLock handle)
        {
            if (target == null || handle == null || owner == null) return;

            try
            {
                var manager = Nivalis.TimeOfDayManager.Instance;
                if (manager == null) return;

                // Only the clock's own pause is interesting.
                var timePause = manager.IsPausedOverrideableBool;
                if (timePause == null || target.Pointer != timePause.Pointer) return;

                // Only the serialiser's. Ours is managed in ClockReplicator, and anything
                // else holding time still is doing it for a reason we have no business
                // overruling.
                if (owner.TryCast<Nivalis.SerializationManager>() == null) return;

                _serialiserLock = handle;
            }
            catch (Exception)
            {
                // Never throw out of a patch on a hot path.
            }
        }

        /// <summary>Forget any tracked lock, for example when leaving a session.</summary>
        public static void Forget() => _serialiserLock = null;

        /// <summary>
        /// If the world is still paused by the serialiser after a load has visibly
        /// finished, let it go. Returns true if it released something.
        /// </summary>
        public static bool ReleaseIfStuck(Action<string> log)
        {
            try
            {
                var manager = Nivalis.TimeOfDayManager.Instance;
                if (manager == null || !manager.IsPaused) return false;

                if (_serialiserLock == null || _serialiserLock.IsReleased)
                {
                    // Paused by somebody we never saw take it. Say who, rather than
                    // silently leaving the player in a stopped world.
                    if (!_warnedNotTracked)
                    {
                        _warnedNotTracked = true;
                        log("Clock: the world is still paused and no tracked load lock " +
                            "explains it. Owners follow.");
                        try { manager.IsPausedOverrideableBool?.LogOwners(); } catch (Exception) { }
                    }
                    return false;
                }

                _serialiserLock.Release();
                _serialiserLock = null;

                var nowPaused = manager.IsPaused;
                log($"Clock: released the serialiser's leftover load pause; " +
                    $"still paused: {nowPaused}");

                if (nowPaused)
                {
                    try { manager.IsPausedOverrideableBool?.LogOwners(); } catch (Exception) { }
                }

                return true;
            }
            catch (Exception ex)
            {
                log($"Clock: could not release the load pause: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Watches every override taken on an <see cref="Nivalis.OverrideableBool"/> so
    /// <see cref="LoadPauseGuard"/> can recognise the one the serialiser takes while loading.
    /// <para>
    /// Patched at <c>OverrideableBool.Override</c> rather than
    /// <c>TimeOfDayManager.Pause</c> because that is the single point every route to a pause
    /// goes through, including whatever the serialiser uses internally. Patching the
    /// convenience wrapper instead would miss a caller that skipped it.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(Nivalis.OverrideableBool), nameof(Nivalis.OverrideableBool.Override),
                  new[] { typeof(Il2CppSystem.Object) })]
    internal static class OverrideableBoolOverridePatch
    {
        [HarmonyPostfix]
        private static void Postfix(Nivalis.OverrideableBool __instance,
                                    Il2CppSystem.Object debugOwner,
                                    Nivalis.OverrideableBool.OverrideLock __result)
        {
            LoadPauseGuard.Record(__instance, debugOwner, __result);
        }
    }
}
