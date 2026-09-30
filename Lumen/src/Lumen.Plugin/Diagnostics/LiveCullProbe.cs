using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// Distance culling that keeps working while you walk around, so a setting can be
    /// judged in play rather than from a standing snapshot.
    /// <para>
    /// Work is spread across frames instead of spiking, the distance bands have hysteresis
    /// so nothing flickers on the boundary, and everything is restored exactly as found.
    /// </para>
    /// </summary>
    internal abstract class LiveCullProbe : Probe
    {
        /// <summary>Distance bands, nearest last. Left/right steps through them.</summary>
        protected abstract float[] Distances { get; }

        /// <summary>Which of a character's renderers this probe is willing to hide.</summary>
        protected abstract bool IsCullable(Renderer renderer);

        /// <summary>
        /// Alternative to hiding: change some other property of the renderer instead.
        /// Return true if this probe handled the renderer, in which case the hide path is
        /// skipped. Implementations own their own original-value bookkeeping.
        /// </summary>
        protected virtual bool OverrideRenderer(Renderer renderer, bool suppress) => false;

        /// <summary>
        /// Called with a character's full renderer list before any of them are considered.
        /// Lets a probe reach a decision that needs the whole set rather than one renderer
        /// at a time - which LOD level is redundant, for instance.
        /// </summary>
        protected virtual void PrepareCharacter(Il2CppArrayBase<Renderer> renderers) { }

        protected int Index;

        internal override bool Adjustable => true;

        // Characters come from CharacterRegistry rather than a scene sweep. Held
        // references are used inside try/catch: a scene unload can invalidate one.
        private const float RefreshInterval = 0.5f;
        private const float Hysteresis = 0.9f;

        private readonly List<BaseCharacter> _characters = new List<BaseCharacter>();
        private readonly HashSet<int> _hiddenCharacters = new HashSet<int>();
        private readonly Dictionary<int, bool> _originalEnabled = new Dictionary<int, bool>();

        private float _sinceRefresh;
        private int _cursor;
        private int _hiddenCount;
        private int _visibleCount;

        protected override void Apply()
        {
            _sinceRefresh = RefreshInterval;   // force a refresh on the first tick
            _cursor = 0;
            Status = $"{Distances[Index]:0}m";
        }

        protected override void Restore()
        {
            RefreshCharacters();

            foreach (var character in _characters)
            {
                try { SetHidden(character, false); }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogWarning($"{Name}: could not restore one character: {ex.Message}");
                }
            }

            _characters.Clear();
            _hiddenCharacters.Clear();
            _originalEnabled.Clear();
            _hiddenCount = 0;
            _visibleCount = 0;

            Status = $"{Distances[Index]:0}m";
        }

        internal override void Tick(float unscaledDeltaTime)
        {
            var camera = Camera.main;
            if (camera == null) return;

            _sinceRefresh += unscaledDeltaTime;
            if (_sinceRefresh >= RefreshInterval)
            {
                _sinceRefresh = 0f;
                RefreshCharacters();
                _cursor = 0;
            }

            if (_characters.Count == 0) return;

            Vector3 eye = camera.transform.position;
            float far = Distances[Index];
            float near = far * Hysteresis;
            float farSq = far * far;
            float nearSq = near * near;

            // One eighth of the crowd per frame: the full set is re-evaluated in about
            // 130ms, and no single frame pays for all of it.
            int slice = Math.Max(8, _characters.Count / 8);

            for (int n = 0; n < slice; n++)
            {
                if (_characters.Count == 0) return;
                if (_cursor >= _characters.Count) _cursor = 0;

                var character = _characters[_cursor++];

                try
                {
                    int id = character.GetInstanceID();
                    bool isHidden = _hiddenCharacters.Contains(id);
                    float distanceSq = (character.transform.position - eye).sqrMagnitude;

                    // Hide past the far edge, reveal inside the near edge, and leave
                    // anything in the band alone so it cannot oscillate.
                    if (!isHidden && distanceSq > farSq) SetHidden(character, true);
                    else if (isHidden && distanceSq < nearSq) SetHidden(character, false);
                }
                catch (Exception)
                {
                    // Almost certainly a character that went away between refreshes.
                    // Drop it; the next refresh rebuilds the list.
                    _characters.RemoveAt(Math.Max(0, _cursor - 1));
                    _cursor = Math.Max(0, _cursor - 1);
                }
            }

            Status = $"{far:0}m  {_hiddenCount} of {_characters.Count} culled";
        }

        private void RefreshCharacters() => CharacterRegistry.CopyInto(_characters);

        private void SetHidden(BaseCharacter character, bool hide)
        {
            int id = character.GetInstanceID();
            bool alreadyHidden = _hiddenCharacters.Contains(id);
            if (hide == alreadyHidden) return;

            var renderers = character.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return;

            PrepareCharacter(renderers);

            int touched = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;

                if (OverrideRenderer(renderer, hide)) { touched++; continue; }
                if (!IsCullable(renderer)) continue;

                int rendererId = renderer.GetInstanceID();

                if (hide)
                {
                    // Most of a character's renderers are dormant LOD variants. Hiding one
                    // that is already off changes nothing and must not be counted.
                    if (!renderer.enabled) continue;

                    // Only ever record the value we found the first time. Re-recording
                    // while overridden would latch "disabled" as the original.
                    if (!_originalEnabled.ContainsKey(rendererId))
                        _originalEnabled[rendererId] = renderer.enabled;

                    renderer.enabled = false;
                    touched++;
                }
                else if (_originalEnabled.TryGetValue(rendererId, out bool wasEnabled))
                {
                    renderer.enabled = wasEnabled;
                    touched++;
                }
            }

            if (touched == 0) return;

            if (hide)
            {
                _hiddenCharacters.Add(id);
                _hiddenCount++;
                _visibleCount = Math.Max(0, _visibleCount - 1);
            }
            else
            {
                _hiddenCharacters.Remove(id);
                _hiddenCount = Math.Max(0, _hiddenCount - 1);
                _visibleCount++;
            }
        }

        internal override bool Adjust(int direction)
        {
            int next = Mathf.Clamp(Index + direction, 0, Distances.Length - 1);
            if (next == Index) return false;

            bool wasActive = Active;
            if (wasActive) Toggle();   // restore at the old distance first
            Index = next;
            if (wasActive) Toggle();

            Status = $"{Distances[Index]:0}m";
            return true;
        }
    }

    /// <summary>
    /// Hides whole NPCs past an adjustable distance. Visible, and off by default, but a
    /// generous distance can be an acceptable trade for the frame rate.
    /// </summary>
    internal sealed class LiveNpcCullProbe : LiveCullProbe
    {
        internal override string Name => "NPC cull distance (live)";
        internal override string Hint =>
            "Whole NPCs, updated as you walk. Find the distance where you stop noticing.";

        protected override float[] Distances =>
            new[] { 150f, 120f, 100f, 80f, 65f, 50f, 40f, 30f, 20f };

        protected override bool IsCullable(Renderer renderer) => true;
    }

    /// <summary>
    /// Hides the non-skinned props bolted to distant NPCs - bags, badges, cups, phones,
    /// jewellery - and leaves the body, clothing and hair alone.
    /// </summary>
    internal sealed class LivePropsCullProbe : LiveCullProbe
    {
        internal override string Name => "NPC props cull (live)";
        internal override string Hint => "Accessories only; bodies stay.";

        protected override float[] Distances => new[] { 60f, 45f, 30f, 20f, 12f };

        protected override bool IsCullable(Renderer renderer) =>
            renderer.TryCast<SkinnedMeshRenderer>() == null;
    }

    /// <summary>
    /// Collapses a character's stacked LOD renderers down to one per part.
    /// <para>
    /// On many characters every LOD level is enabled at once - <c>Body_LOD_0</c> through
    /// <c>Body_LOD_004</c>, same bounds, same position - so the same body is drawn five
    /// times, each copy uploading a full set of bone matrices every frame.
    /// </para>
    /// <para>
    /// Dropping the extra copies is invisible: they are the same shape in the same place,
    /// and the most detailed one is the one kept.
    /// </para>
    /// </summary>
    internal sealed class LiveLodStackCullProbe : LiveCullProbe
    {
        internal override string Name => "Collapse stacked NPC LODs";
        internal override string Hint =>
            "Keeps the highest-detail LOD per part, drops the duplicates underneath it. " +
            "0m means every character, including the one in front of you.";

        // Distances mean "cull beyond this", so 0m covers every character.
        protected override float[] Distances => new[] { 30f, 12f, 5f, 0f };

        private const string Marker = "_LOD_";

        private readonly HashSet<int> _redundant = new HashSet<int>();

        protected override void PrepareCharacter(Il2CppArrayBase<Renderer> renderers)
        {
            _redundant.Clear();

            // Group by the name before "_LOD_", then keep the lowest-numbered level in each
            // group and mark the rest redundant. Lowest number is the highest detail.
            var best = new Dictionary<string, int>();      // group -> best level seen
            var bestId = new Dictionary<string, int>();    // group -> renderer id of that level

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    if (renderer == null) continue;

                    try
                    {
                        string name = renderer.name;
                        int marker = name.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
                        if (marker < 0) continue;

                        string group = name.Substring(0, marker).ToLowerInvariant();
                        string suffix = name.Substring(marker + Marker.Length);

                        if (!int.TryParse(suffix, out int level)) continue;

                        int id = renderer.GetInstanceID();

                        if (pass == 0)
                        {
                            // First pass finds the winner for each group.
                            if (!best.TryGetValue(group, out int current) || level < current)
                            {
                                best[group] = level;
                                bestId[group] = id;
                            }
                        }
                        else if (bestId.TryGetValue(group, out int keepId) && id != keepId)
                        {
                            _redundant.Add(id);
                        }
                    }
                    catch (Exception) { /* odd name; leave it alone */ }
                }
            }
        }

        protected override bool IsCullable(Renderer renderer)
        {
            try { return _redundant.Contains(renderer.GetInstanceID()); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Drops the dedicated shadow-proxy renderers characters carry.
    /// <para>
    /// Each part is duplicated as a <c>*_Shadow</c> renderer alongside its LOD levels, so
    /// every character carries several extra skinned meshes whose only job is to cast a
    /// shadow. Almost no light in these scenes casts one.
    /// </para>
    /// </summary>
    internal sealed class LiveShadowProxyCullProbe : LiveCullProbe
    {
        internal override string Name => "NPC shadow proxies off";
        internal override string Hint =>
            "Three extra skinned renderers per character, all uploading bone matrices. " +
            "Only one light in the scene casts shadows and disabling shadows entirely cost " +
            "0.1ms, so these should be invisible. Start at 150m: that culls everyone.";

        protected override float[] Distances => new[] { 150f, 60f, 30f, 12f };

        protected override bool IsCullable(Renderer renderer)
        {
            try { return renderer.name.EndsWith("_Shadow", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Leaves every character renderer in place but stops them being submitted to the
    /// shadow pass. Separates "the proxy meshes cost to draw" from "the proxy meshes cost
    /// because something is still rendering shadows with them".
    /// </summary>
    internal sealed class LiveShadowCastingOffProbe : LiveCullProbe
    {
        internal override string Name => "NPC shadow casting off";
        internal override string Hint =>
            "Same target as the probe above, approached from the other side. Compare the two.";

        protected override float[] Distances => new[] { 150f, 60f, 30f, 12f };

        // Handled wholesale in OverrideRenderer rather than by hiding anything.
        protected override bool IsCullable(Renderer renderer) => false;

        private readonly Dictionary<int, ShadowCastingMode> _originalMode =
            new Dictionary<int, ShadowCastingMode>();

        protected override bool OverrideRenderer(Renderer renderer, bool suppress)
        {
            try
            {
                int id = renderer.GetInstanceID();

                if (suppress)
                {
                    if (renderer.shadowCastingMode == ShadowCastingMode.Off) return false;

                    if (!_originalMode.ContainsKey(id))
                        _originalMode[id] = renderer.shadowCastingMode;

                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    return true;
                }

                // Restore the mode we found, never a guessed default.
                if (!_originalMode.TryGetValue(id, out var original)) return false;

                renderer.shadowCastingMode = original;
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Hides physically small parts of distant NPCs regardless of renderer type. Catches
    /// detail meshes that happen to be skinned, which the props probe leaves behind.
    /// </summary>
    internal sealed class LiveSmallPartCullProbe : LiveCullProbe
    {
        private const float MaxDiagonal = 0.35f;

        internal override string Name => "NPC small parts cull (live)";
        internal override string Hint =>
            $"Anything under {MaxDiagonal:0.00}m across. A few pixels at these ranges.";

        protected override float[] Distances => new[] { 60f, 45f, 30f, 20f, 12f };

        protected override bool IsCullable(Renderer renderer)
        {
            try { return renderer.bounds.size.magnitude < MaxDiagonal; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Everything that measured as free, applied together.
    /// <para>
    /// The individual deltas cannot simply be added up: these all cut renderers off the
    /// same characters, so they overlap to an unknown degree. The only honest way to know
    /// what the combination is worth is to measure the combination, which is what this is
    /// for. It is also the exact set that would ship, so the number it produces is the
    /// number that matters.
    /// </para>
    /// </summary>
    internal sealed class LumenPresetProbe : Probe
    {
        private readonly LiveCullProbe[] _parts =
        {
            new LiveLodStackCullProbe(),
            new LiveShadowProxyCullProbe(),
            new LiveSmallPartCullProbe()
        };

        internal override string Name => "== LUMEN PRESET (all free wins) ==";
        internal override string Hint =>
            "Stacked LODs + shadow proxies + small parts, all at once. This is the shipping " +
            "candidate. Walk a crowd with it on and try to catch it.";

        protected override void Apply()
        {
            int applied = 0;
            foreach (var part in _parts)
            {
                if (part.Active) continue;
                if (part.Toggle()) applied++;
            }

            Status = $"{applied} of {_parts.Length} on";
        }

        protected override void Restore()
        {
            foreach (var part in _parts)
            {
                if (part.Active) part.ForceRestore();
            }

            Status = null;
        }

        internal override void Tick(float unscaledDeltaTime)
        {
            foreach (var part in _parts)
            {
                if (!part.Active) continue;

                // One misbehaving part must not stop the others or take the frame down.
                try { part.Tick(unscaledDeltaTime); }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogError($"{Name}: '{part.Name}' failed, restoring it: {ex}");
                    part.ForceRestore();
                }
            }
        }
    }

    /// <summary>
    /// Redundant LOD copies and shadow proxies, removed in a single pass over each
    /// character. This mirrors what the optimizer does at runtime.
    /// <para>
    /// One pass rather than two: each pass walks the crowd and calls
    /// <c>GetComponentsInChildren</c> on every transition, and that cost is large enough
    /// to cancel out the saving if it is paid twice.
    /// </para>
    /// </summary>
    internal sealed class LiveNpcCleanupProbe : LiveCullProbe
    {
        internal override string Name => "== NPC renderer cleanup (ship this) ==";
        internal override string Hint =>
            "Stacked LOD copies and shadow proxies, one pass. 0m means every character.";

        protected override float[] Distances => new[] { 30f, 12f, 5f, 0f };

        private const string LodMarker = "_LOD_";
        private const string ShadowSuffix = "_Shadow";

        private readonly HashSet<int> _redundant = new HashSet<int>();

        protected override void PrepareCharacter(Il2CppArrayBase<Renderer> renderers)
        {
            _redundant.Clear();

            var bestLevel = new Dictionary<string, int>();
            var bestId = new Dictionary<string, int>();

            // First pass picks the winner per LOD group, second marks the losers. Lowest
            // level number is the highest detail, and that is the one kept.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    if (renderer == null) continue;

                    try
                    {
                        string name = renderer.name;

                        if (pass == 1 && name.EndsWith(ShadowSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            _redundant.Add(renderer.GetInstanceID());
                            continue;
                        }

                        int marker = name.LastIndexOf(LodMarker, StringComparison.OrdinalIgnoreCase);
                        if (marker < 0) continue;

                        string group = name.Substring(0, marker).ToLowerInvariant();
                        if (!int.TryParse(name.Substring(marker + LodMarker.Length), out int level))
                            continue;

                        int id = renderer.GetInstanceID();

                        if (pass == 0)
                        {
                            if (!bestLevel.TryGetValue(group, out int current) || level < current)
                            {
                                bestLevel[group] = level;
                                bestId[group] = id;
                            }
                        }
                        else if (bestId.TryGetValue(group, out int keepId) && id != keepId)
                        {
                            _redundant.Add(id);
                        }
                    }
                    catch (Exception) { /* odd name; leave it alone */ }
                }
            }
        }

        protected override bool IsCullable(Renderer renderer)
        {
            try { return _redundant.Contains(renderer.GetInstanceID()); }
            catch (Exception) { return false; }
        }
    }
}
