using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Lumen.Tuning
{
    /// <summary>
    /// Works out which of a character's renderers can be switched off without changing what
    /// the character looks like. Shared by the optimizer and the measurement probes so both
    /// agree on what is removable.
    /// </summary>
    /// <remarks>
    /// This used to also collapse each part's LOD levels down to the most detailed one, on
    /// the belief that characters were drawing all five at once. That was a misreading: a
    /// LODGroup leaves every level's <c>enabled</c> flag true and picks one to draw per
    /// frame, so several enabled LOD renderers is not several being drawn. Switching off the
    /// level the LODGroup had chosen simply made characters vanish, and on this game every
    /// character LOD renderer is LODGroup-managed, so there was nothing to win either.
    /// </remarks>
    internal static class CharacterRenderers
    {
        private const string ShadowSuffix = "_Shadow";

        /// <summary>
        /// Fills <paramref name="into"/> with the instance IDs of removable renderers.
        /// </summary>
        /// <param name="dropShadowProxies">
        /// Drop the dedicated <c>*_Shadow</c> meshes. These are separate skinned meshes whose
        /// only job is to cast a shadow, and NPCs show no shadow with them enabled at any
        /// time of day. They are not LOD levels, so nothing else is managing them.
        /// </param>
        internal static void FindRedundant(Il2CppArrayBase<Renderer> renderers, HashSet<int> into,
            bool dropShadowProxies)
        {
            into.Clear();
            if (renderers == null || !dropShadowProxies) return;

            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;

                try
                {
                    if (!renderer.name.EndsWith(ShadowSuffix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    into.Add(renderer.GetInstanceID());
                }
                catch (Exception)
                {
                    // A renderer that went away mid-walk; leave it alone.
                }
            }
        }
    }
}
