using System;
using HarmonyLib;

namespace Nightshare.Patches
{
    /// <summary>
    /// Traces the game's own load sequence so a stalled load names its own cause.
    /// <para>
    /// <b>Why this exists.</b> A guest receives the host's save, writes it and calls
    /// <c>SerializationManager.Load</c>, and then sits on a loading screen forever. The
    /// transfer is provably fine on both sides, so the failure is somewhere inside a
    /// sequence we do not drive and cannot see. This was guessed at four times and the
    /// guesses cost more than the instrumentation would have.
    /// </para>
    /// <para>
    /// <b>How to use it.</b> Load a save normally from the main menu first and keep that
    /// trace as the baseline, because it is the sequence the developers test. Then join as
    /// a guest and diff the two. The first line present in one and missing in the other is
    /// the bug, and no further reasoning is required to find it.
    /// </para>
    /// <para>
    /// This is diagnostics only. Every patch is a passive observer that logs and returns,
    /// so removing the file changes no behaviour.
    /// </para>
    /// </summary>
    internal static class LoadPathTrace
    {
        /// <summary>
        /// Wall-clock start of the load in flight, for elapsed times in the trace. Null
        /// when nothing is loading.
        /// </summary>
        private static DateTime? _startedAt;

        private static float _sinceHeartbeat;
        private static string _lastHeartbeat;

        private static void Trace(string message)
        {
            var at = _startedAt.HasValue
                ? $"+{(DateTime.Now - _startedAt.Value).TotalSeconds,6:0.00}s "
                : "             ";

            NightshareLog.Write("LOAD", at + message);
            NightsharePlugin.Logger?.LogInfo($"[load] {at}{message}");
        }

        /// <summary>
        /// Heartbeat while a load is in flight, driven from the runner.
        /// <para>
        /// A stalled coroutine produces no events at all, so without this the trace simply
        /// stops and cannot distinguish "stuck" from "finished and quiet". Reporting the
        /// two state flags once a second says which, and only logs when they change so a
        /// long load does not bury the trace.
        /// </para>
        /// </summary>
        public static void Tick(float deltaTime)
        {
            if (_startedAt == null) return;

            _sinceHeartbeat += deltaTime;
            if (_sinceHeartbeat < 1f) return;
            _sinceHeartbeat = 0f;

            try
            {
                var initialising = "?";
                try
                {
                    var manager = Nivalis.SerializationManager.Instance;
                    initialising = manager == null ? "no-instance" : manager.IsInitializing.ToString();
                }
                catch (Exception) { }

                var screen = "?";
                try { screen = Nivalis.LoadingScreenUI.showing.ToString(); } catch (Exception) { }

                var scene = "?";
                try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
                catch (Exception) { }

                var line = $"IsInitializing={initialising}  LoadingScreenUI.showing={screen}  scene={scene}";

                // Only on change, plus the elapsed time, so a stall reads as one line and
                // an actual stall is obvious from the gap in timestamps either side.
                if (line != _lastHeartbeat)
                {
                    _lastHeartbeat = line;
                    Trace("  " + line);
                }
            }
            catch (Exception) { }
        }

        private static void Begin(string why)
        {
            _startedAt = DateTime.Now;
            _sinceHeartbeat = 0f;
            _lastHeartbeat = null;
            Trace($"==== {why} ====");
        }

        private static void End(string why)
        {
            Trace($"==== {why} ====");
            _startedAt = null;
            _lastHeartbeat = null;
        }

        // ------------------------------------------------------------ the sequence

        [HarmonyPatch(typeof(Nivalis.SerializationManager), nameof(Nivalis.SerializationManager.Load))]
        private static class SerializationManagerLoad
        {
            [HarmonyPrefix]
            private static void Prefix(string saveName)
            {
                try { Begin($"SerializationManager.Load('{saveName}') entered"); }
                catch (Exception) { }

                // Every load reports where it put the player, and clears any pause left
                // behind by the load once it settles.
                try
                {
                    var fromMenu = _nextLoadIsFromTheMenu;
                    _nextLoadIsFromTheMenu = false;
                    NightshareCore.Instance.OnLoadStarted(saveName, fromMenu);
                }
                catch (Exception) { }
            }

            /// <summary>
            /// <c>Load</c> starts a coroutine, so returning here means the routine was
            /// <i>launched</i>, not that the world is up. Said explicitly because reading
            /// this line as completion is exactly the mistake that hid the bug.
            /// </summary>
            [HarmonyPostfix]
            private static void Postfix(string saveName)
            {
                try { Trace($"SerializationManager.Load('{saveName}') returned (coroutine started, not finished)"); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// The clearest probe of the two. The flag brackets the real work, so a load that
        /// never clears it is stuck inside, and one that never sets it never began.
        /// </summary>
        [HarmonyPatch(typeof(Nivalis.SerializationManager), "set_IsInitializing")]
        private static class IsInitialising
        {
            [HarmonyPostfix]
            private static void Postfix(bool value)
            {
                try
                {
                    Trace($"SerializationManager.IsInitializing = {value}");
                    if (!value && _startedAt != null) End("initialisation finished");
                }
                catch (Exception) { }
            }
        }

        [HarmonyPatch(typeof(Nivalis.SerializationManager), "OnSceneLoaded")]
        private static class SceneLoaded
        {
            [HarmonyPostfix]
            private static void Postfix(UnityEngine.SceneManagement.Scene scene,
                                        UnityEngine.SceneManagement.LoadSceneMode mode)
            {
                try
                {
                    Trace($"scene loaded: '{scene.name}' ({mode})");

                    // THE READINESS SIGNAL THE ARRIVAL WAITS ON.
                    //
                    // A city district loading additively is the point at which there is a
                    // world to be placed in. The infrastructure scenes are not: '_Global'
                    // is always there and 'Logo_Screen' is the menu, so neither means a
                    // guest has somewhere to arrive.
                    var name = scene.name ?? "";
                    if (mode == UnityEngine.SceneManagement.LoadSceneMode.Additive
                        && name != "_Global" && name != "Logo_Screen")
                    {
                        NightshareCore.Instance.OnGameplaySceneLoaded();
                    }
                }
                catch (Exception) { }
            }
        }

        [HarmonyPatch(typeof(Nivalis.LoadingScreenUI), nameof(Nivalis.LoadingScreenUI.Show))]
        private static class LoadingScreenShow
        {
            [HarmonyPrefix]
            private static void Prefix(bool isGameplayLoading, Il2CppSystem.Action onDone)
            {
                try
                {
                    Trace($"LoadingScreenUI.Show(isGameplayLoading: {isGameplayLoading}, " +
                          $"onDone: {(onDone == null ? "null" : "set")})");
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Holds the loading screen up while a joining guest is still being placed.
        /// <para>
        /// Without this the screen drops the moment the world is loaded, showing two seconds
        /// of the zone's arrival point followed by a teleport to the host. It works, and it
        /// looks like it does not.
        /// </para>
        /// </summary>
        [HarmonyPatch(typeof(Nivalis.LoadingScreenUI), nameof(Nivalis.LoadingScreenUI.Hide))]
        private static class LoadingScreenHide
        {
            private static bool _loggedHold;

            /// <summary>Returning false skips the original, leaving the screen up.</summary>
            [HarmonyPrefix]
            private static bool Prefix()
            {
                try
                {
                    if (NightshareCore.Instance.ShouldHoldLoadingScreen())
                    {
                        // Hide can be called repeatedly; say it once.
                        if (!_loggedHold)
                        {
                            _loggedHold = true;
                            Trace("LoadingScreenUI.Hide() HELD, the guest is not placed yet");
                        }
                        return false;
                    }

                    _loggedHold = false;
                    Trace("LoadingScreenUI.Hide()");
                }
                catch (Exception)
                {
                    // A fault here must never be able to trap a player behind the screen.
                    return true;
                }

                return true;
            }
        }

        /// <summary>
        /// The game's own entry point, for the baseline run. A guest join should end up
        /// producing the same downstream trace as this does.
        /// </summary>
        /// <summary>
        /// Set by the load button, read and cleared by the next <c>Load</c>. This is what
        /// lets an arrival line say whether it came from the game's own menu or from us,
        /// which is the difference a control run exists to measure.
        /// </summary>
        private static bool _nextLoadIsFromTheMenu;

        [HarmonyPatch(typeof(Nivalis.LoadUI), "LoadRequest")]
        private static class LoadUiRequest
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                try
                {
                    _nextLoadIsFromTheMenu = true;
                    Trace("LoadUI.LoadRequest()  <-- the game's own load button");
                }
                catch (Exception) { }
            }
        }
    }
}
