using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Lumen.Tuning
{
    /// <summary>
    /// Works out which of a character's renderers are redundant. Shared by the optimizer
    /// and the measurement probes so both agree on what can be removed.
    /// </summary>
    internal static class CharacterRenderers
    {
        private const string LodMarker = "_LOD_";
        private const string ShadowSuffix = "_Shadow";

        // Scratch, reused across calls: this runs on a slice of the crowd every frame.
        private static readonly Dictionary<string, int> BestLevel = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> BestId = new Dictionary<string, int>();

        /// <summary>
        /// Fills <paramref name="into"/> with the instance IDs of renderers that can be
        /// switched off without changing what the character looks like.
        /// </summary>
        /// <param name="collapseLods">
        /// Drop every LOD level but the most detailed one. Characters enable all five
        /// levels at once: the same mesh drawn five times, each uploading a full set of
        /// bone matrices.
        /// </param>
        /// <param name="dropShadowProxies">
        /// Drop the dedicated <c>*_Shadow</c> meshes. NPCs cast no visible shadow with
        /// these enabled, at any time of day.
        /// </param>
        internal static void FindRedundant(Il2CppArrayBase<Renderer> renderers, HashSet<int> into,
            bool collapseLods, bool dropShadowProxies)
        {
            into.Clear();
            if (renderers == null) return;

            BestLevel.Clear();
            BestId.Clear();

            // Two passes: the first finds the surviving level for each LOD group, the second
            // marks everything else. One pass cannot work - the winner may appear last.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    if (renderer == null) continue;

                    try
                    {
                        string name = renderer.name;

                        if (pass == 1 && dropShadowProxies &&
                            name.EndsWith(ShadowSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            into.Add(renderer.GetInstanceID());
                            continue;
                        }

                        if (!collapseLods) continue;

                        int marker = name.LastIndexOf(LodMarker, StringComparison.OrdinalIgnoreCase);
                        if (marker < 0) continue;

                        string group = name.Substring(0, marker).ToLowerInvariant();
                        if (!int.TryParse(name.Substring(marker + LodMarker.Length), out int level))
                            continue;

                        // Only renderers that are currently drawing are candidates.
                        //
                        // This is load bearing. Where the game's own LODGroup is working it
                        // has already chosen a level, and that choice is not always level 0
                        // - a distant character may legitimately be showing LOD_002. Taking
                        // the lowest-numbered renderer regardless of its state would hide
                        // the visible one and keep one that was already off.
                        //
                        // Considering only enabled renderers makes this safe by construction:
                        // a group can go from several drawing to exactly one, never to none.
                        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;

                        int id = renderer.GetInstanceID();

                        if (pass == 0)
                        {
                            // Among the ones actually drawing, keep the most detailed.
                            if (!BestLevel.TryGetValue(group, out int current) || level < current)
                            {
                                BestLevel[group] = level;
                                BestId[group] = id;
                            }
                        }
                        else if (BestId.TryGetValue(group, out int keepId) && id != keepId)
                        {
                            into.Add(id);
                        }
                    }
                    catch (Exception)
                    {
                        // An odd name or a renderer that went away; leave it alone.
                    }
                }
            }
        }
    }
}
