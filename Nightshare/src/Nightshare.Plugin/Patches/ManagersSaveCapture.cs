using System;
using HarmonyLib;
using Nivalis;

namespace Nightshare.Patches
{
    /// <summary>
    /// Holds a reference to the live <see cref="ManagersSave"/>.
    /// <para>
    /// <c>ManagersSave</c> is the root of the game's entire save system: a
    /// <c>Dictionary&lt;Type, IManager&gt;</c> plus the matching save packets. Every world
    /// snapshot is built by walking it, so the mod needs a handle on the instance.
    /// </para>
    /// <para>
    /// It is a plain object rather than a MonoBehaviour, so it cannot be found with
    /// FindObjectOfType and there is no singleton to ask. Catching it as it is constructed
    /// is the way in.
    /// </para>
    /// </summary>
    [HarmonyPatch]
    internal static class ManagersSaveCapture
    {
        /// <summary>The live instance, or null before a world has loaded.</summary>
        public static ManagersSave Current { get; private set; }

        /// <summary>Managers registered at construction, for the log.</summary>
        public static int ManagerCount { get; private set; }

        /// <summary>
        /// Caught on save, which is the only hook that works.
        /// <para>
        /// <b>Do not add a constructor patch here.</b> There was one, and Il2CppInterop
        /// refuses it outright with <c>"Failed to init IL2CPP patch backend for
        /// ManagersSave::.ctor: Derived classes must provide an implementation"</c>,
        /// repeated on every attempt. Harmony still reported the patch as bound, so it
        /// looked fine while doing nothing at all.
        /// </para>
        /// <para>
        /// Nothing depends on this any more. Managers are found by scanning the scene for
        /// MonoBehaviours implementing <c>IManager</c>, which is more robust than any
        /// capture. This survives only as a cross-check on that scan's count.
        /// </para>
        /// </summary>
        [HarmonyPatch(typeof(ManagersSave), nameof(ManagersSave.Save))]
        [HarmonyPostfix]
        private static void OnSaved(ManagersSave __instance)
        {
            // A Harmony patch must never throw into IL2CPP's native call stack.
            try
            {
                if (Current != null) return;

                Current = __instance;
                try { ManagerCount = __instance.Managers.Count; }
                catch (Exception) { ManagerCount = -1; }
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogError($"ManagersSaveCapture failed on save: {ex}");
            }
        }
    }
}
