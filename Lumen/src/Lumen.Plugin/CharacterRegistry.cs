using System;
using System.Collections.Generic;
using HarmonyLib;
using Nivalis;
using UnityEngine;

namespace Lumen
{
    /// <summary>
    /// A live list of the characters in the world, maintained by hooking their enable and
    /// disable rather than by sweeping the scene.
    /// <para>
    /// <c>FindObjectsOfType</c> scans every loaded object, and this game has around 830,000
    /// of them, so a sweep costs milliseconds. Hooking is the only way to keep a current
    /// list without paying that repeatedly.
    /// </para>
    /// </summary>
    internal static class CharacterRegistry
    {
        private static readonly List<BaseCharacter> Live = new List<BaseCharacter>();

        /// <summary>
        /// Raised as a character is disabled, before it goes back to the pool.
        /// <para>
        /// Characters are pooled rather than destroyed, and a recycled one returns with a
        /// different set of body parts. Anything hidden on its previous life has to be
        /// handed back before it is reused.
        /// </para>
        /// </summary>
        internal static event Action<BaseCharacter> Disabled;
        private static bool _hooked;

        /// <summary>True once the enable/disable hooks are in place and the list is authoritative.</summary>
        internal static bool Hooked => _hooked;

        internal static int Count => Live.Count;

        internal static void Install(Harmony harmony)
        {
            try
            {
                // Bound by name, never by offset: offsets move on every game patch.
                var onEnable = AccessTools.Method(typeof(Character), "OnEnable");
                var onDisable = AccessTools.Method(typeof(Character), "OnDisable");

                if (onEnable == null || onDisable == null)
                {
                    LumenPlugin.Log.LogWarning(
                        "Character.OnEnable/OnDisable not found; falling back to scene sweeps. " +
                        "This usually means the game has been patched.");
                    return;
                }

                harmony.Patch(onEnable,
                    postfix: new HarmonyMethod(typeof(CharacterRegistry), nameof(AfterEnable)));
                harmony.Patch(onDisable,
                    postfix: new HarmonyMethod(typeof(CharacterRegistry), nameof(AfterDisable)));

                _hooked = true;
                LumenPlugin.Log.LogInfo("Character registry hooked.");
            }
            catch (Exception ex)
            {
                // A failed patch logs loudly and carries on; the fallback still works.
                LumenPlugin.Log.LogError($"Could not hook the character registry: {ex}");
            }
        }

        private static void AfterEnable(Character __instance)
        {
            // An exception thrown into IL2CPP's native call stack is a crash, not a trace.
            try
            {
                if (__instance == null) return;
                Live.Add(__instance);
            }
            catch (Exception) { }
        }

        private static void AfterDisable(Character __instance)
        {
            try
            {
                if (__instance == null) return;

                try { Disabled?.Invoke(__instance); }
                catch (Exception ex) { LumenPlugin.Log.LogError($"Character disable handler failed: {ex}"); }

                int id = __instance.GetInstanceID();
                for (int i = Live.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (Live[i].GetInstanceID() == id) { Live.RemoveAt(i); return; }
                    }
                    catch (Exception)
                    {
                        // Entry has gone bad; drop it while we are here.
                        Live.RemoveAt(i);
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Fills <paramref name="into"/> with the current characters. Uses the hooked list
        /// when available and falls back to a scene sweep when the patch did not land.
        /// </summary>
        internal static void CopyInto(List<BaseCharacter> into)
        {
            into.Clear();

            if (!_hooked)
            {
                into.AddRange(Diagnostics.Find.All<BaseCharacter>());
                return;
            }

            for (int i = Live.Count - 1; i >= 0; i--)
            {
                try
                {
                    var character = Live[i];
                    if (character == null) { Live.RemoveAt(i); continue; }
                    into.Add(character);
                }
                catch (Exception)
                {
                    Live.RemoveAt(i);
                }
            }
        }

        internal static void Clear() => Live.Clear();
    }
}
