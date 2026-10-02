using System;
using System.Collections.Generic;
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
    /// A guest's clock appeared to work only because the host's sync was force-adding time
    /// to it every second; leaving the session stopped that and the world froze.
    /// </para>
    /// <para>
    /// <b>Releasing someone else's lock is defensible only because of the window.</b>
    /// Nothing is touched unless it was taken <i>during a load</i> and is still held after
    /// that load has visibly ended, at which point it is stale by definition. Locks taken
    /// at any other time belong to whoever took them.
    /// </para>
    /// </summary>
    internal static class LoadPauseGuard
    {
        private sealed class Held
        {
            internal Nivalis.OverrideableBool.OverrideLock Handle;
            internal string Owner;
        }

        private static readonly List<Held> Taken = new List<Held>();
        private static bool _loading;

        /// <summary>A load has started. Anything that pauses time from here is suspect.</summary>
        public static void LoadStarted()
        {
            _loading = true;
            Taken.Clear();
        }

        /// <summary>Drop everything, for example on leaving a session.</summary>
        public static void Forget()
        {
            _loading = false;
            Taken.Clear();
        }

        /// <summary>
        /// Remember a lock taken against the time-of-day pause during a load.
        /// <para>
        /// <c>Override</c> is how every gate in the game is held, including player movement
        /// and menu shortcuts, so this runs on a hot path. The <c>_loading</c> check is
        /// first precisely because it is false almost always and costs nothing.
        /// </para>
        /// </summary>
        public static void Record(Nivalis.OverrideableBool target,
                                  Il2CppSystem.Object owner,
                                  Nivalis.OverrideableBool.OverrideLock handle)
        {
            if (!_loading || target == null || handle == null) return;

            try
            {
                var manager = Nivalis.TimeOfDayManager.Instance;
                if (manager == null) return;

                var timePause = manager.IsPausedOverrideableBool;
                if (timePause == null || target.Pointer != timePause.Pointer) return;

                Taken.Add(new Held { Handle = handle, Owner = Describe(owner) });
            }
            catch (Exception)
            {
                // Never throw out of a patch on a hot path.
            }
        }

        private static string Describe(Il2CppSystem.Object owner)
        {
            if (owner == null) return "null";
            try { return owner.GetIl2CppType()?.FullName ?? "unknown"; }
            catch (Exception) { return "unknown"; }
        }

        /// <summary>
        /// The load has visibly finished. Let go of anything it left holding time still.
        /// <para>
        /// Reports through the caller's log rather than the game's <c>LogOwners</c>, because
        /// that writes to Unity's log and two instances share one of those: a guest's copy
        /// of it is silently discarded, which is exactly the instance whose clock is stuck.
        /// </para>
        /// </summary>
        public static void ReleaseStaleLoadPauses(Action<string> log)
        {
            _loading = false;

            try
            {
                var manager = Nivalis.TimeOfDayManager.Instance;
                if (manager == null) return;

                if (!manager.IsPaused)
                {
                    Taken.Clear();
                    return;
                }

                if (Taken.Count == 0)
                {
                    log("Clock: still paused after the load, and nothing took a time pause " +
                        "during it. The owner is something this guard cannot see.");
                    return;
                }

                var released = 0;
                foreach (var held in Taken)
                {
                    try
                    {
                        if (held.Handle == null || held.Handle.IsReleased) continue;
                        held.Handle.Release();
                        released++;
                        log($"Clock: released a load pause held by {held.Owner}");
                    }
                    catch (Exception ex)
                    {
                        log($"Clock: could not release {held.Owner}'s load pause: {ex.Message}");
                    }
                }

                Taken.Clear();
                log($"Clock: {released} stale load pause(s) released; still paused: {manager.IsPaused}");
            }
            catch (Exception ex)
            {
                log($"Clock: releasing load pauses threw: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// <c>Override(owner)</c>, the overload that returns the lock.
    /// </summary>
    [HarmonyPatch(typeof(Nivalis.OverrideableBool), nameof(Nivalis.OverrideableBool.Override),
                  new[] { typeof(Il2CppSystem.Object) })]
    internal static class OverrideReturningPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Nivalis.OverrideableBool __instance,
                                    Il2CppSystem.Object debugOwner,
                                    Nivalis.OverrideableBool.OverrideLock __result)
        {
            LoadPauseGuard.Record(__instance, debugOwner, __result);
        }
    }

    /// <summary>
    /// <c>Override(owner, out lock)</c>.
    /// <para>
    /// <b>Both overloads must be patched.</b> A first attempt covered only the returning one
    /// and caught nothing at all: the guard reported that no tracked lock explained a world
    /// that was demonstrably still paused. Which overload a caller happens to use is not
    /// something to guess at from the outside.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(Nivalis.OverrideableBool), nameof(Nivalis.OverrideableBool.Override),
                  new[] { typeof(Il2CppSystem.Object), typeof(Nivalis.OverrideableBool.OverrideLock) },
                  new[] { ArgumentType.Normal, ArgumentType.Out })]
    internal static class OverrideOutParamPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Nivalis.OverrideableBool __instance,
                                    Il2CppSystem.Object debugOwner,
                                    ref Nivalis.OverrideableBool.OverrideLock interactabilityLock)
        {
            LoadPauseGuard.Record(__instance, debugOwner, interactabilityLock);
        }
    }
}
