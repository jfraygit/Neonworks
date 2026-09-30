using System;
using System.Collections.Generic;
using Nivalis;
using UnityEngine;

namespace Lumen.Tuning
{
    /// <summary>
    /// Removes redundant NPC renderer work. Always on, config driven.
    /// <para>
    /// Characters render every LOD level of every body part simultaneously, and carry
    /// dedicated skinned meshes whose only job is to cast a shadow the scene's lighting
    /// never shows. Both are removed here.
    /// </para>
    /// <para>
    /// All of it happens in a single pass spread across frames. Separate passes would each
    /// pay their own traversal cost, which is large enough to cancel out the saving.
    /// </para>
    /// </summary>
    internal sealed class NpcOptimizer
    {
        private enum State { Untouched, Cleaned, Culled }

        private const float RefreshInterval = 0.5f;
        private const float Hysteresis = 0.9f;

        private readonly List<BaseCharacter> _characters = new List<BaseCharacter>();
        private readonly Dictionary<int, State> _state = new Dictionary<int, State>();
        private readonly Dictionary<int, bool> _originalEnabled = new Dictionary<int, bool>();
        private readonly HashSet<int> _redundant = new HashSet<int>();

        private float _sinceRefresh = RefreshInterval;
        private int _cursor;

        internal NpcOptimizer()
        {
            CharacterRegistry.Disabled += Release;
        }

        internal int CleanedCount { get; private set; }
        internal int CulledCount { get; private set; }

        internal void Tick(float unscaledDeltaTime)
        {
            if (!LumenConfig.NpcOptimizerEnabled.Value) return;

            var camera = Camera.main;
            if (camera == null) return;

            _sinceRefresh += unscaledDeltaTime;
            if (_sinceRefresh >= RefreshInterval)
            {
                _sinceRefresh = 0f;
                CharacterRegistry.CopyInto(_characters);
                _cursor = 0;
            }

            if (_characters.Count == 0) return;

            Vector3 eye = camera.transform.position;

            float cullDistance = LumenConfig.NpcCullDistance.Value;
            bool cullEnabled = cullDistance > 0f;
            float farSq = cullDistance * cullDistance;
            float nearSq = cullDistance * Hysteresis * cullDistance * Hysteresis;

            // An eighth of the crowd per frame: the full set is re-evaluated in about
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
                    _state.TryGetValue(id, out State current);

                    float distanceSq = (character.transform.position - eye).sqrMagnitude;

                    State wanted;
                    if (!cullEnabled) wanted = State.Cleaned;
                    else if (current == State.Culled)
                        wanted = distanceSq < nearSq ? State.Cleaned : State.Culled;
                    else
                        wanted = distanceSq > farSq ? State.Culled : State.Cleaned;

                    if (wanted != current) ApplyState(character, id, current, wanted);
                }
                catch (Exception)
                {
                    // A character that went away between refreshes. Drop it and move on;
                    // the next refresh rebuilds the list.
                    int index = Math.Max(0, _cursor - 1);
                    if (index < _characters.Count) _characters.RemoveAt(index);
                    _cursor = index;
                }
            }
        }

        private void ApplyState(BaseCharacter character, int id, State from, State to)
        {
            var renderers = character.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return;

            if (to == State.Culled)
            {
                _redundant.Clear();
            }
            else
            {
                CharacterRenderers.FindRedundant(renderers, _redundant,
                    LumenConfig.CollapseStackedLods.Value,
                    LumenConfig.RemoveShadowProxies.Value);
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;

                bool shouldHide = to == State.Culled || _redundant.Contains(renderer.GetInstanceID());
                int rendererId = renderer.GetInstanceID();

                if (shouldHide)
                {
                    // Record the value found the first time only, or an already-hidden
                    // renderer would latch "disabled" as its original state.
                    if (!renderer.enabled)
                    {
                        if (!_originalEnabled.ContainsKey(rendererId))
                            _originalEnabled[rendererId] = false;
                        continue;
                    }

                    if (!_originalEnabled.ContainsKey(rendererId))
                        _originalEnabled[rendererId] = true;

                    renderer.enabled = false;
                }
                else if (_originalEnabled.TryGetValue(rendererId, out bool wasEnabled))
                {
                    renderer.enabled = wasEnabled;
                    _originalEnabled.Remove(rendererId);
                }
            }

            _state[id] = to;

            if (from == State.Culled) CulledCount = Math.Max(0, CulledCount - 1);
            if (from == State.Cleaned) CleanedCount = Math.Max(0, CleanedCount - 1);
            if (to == State.Culled) CulledCount++;
            if (to == State.Cleaned) CleanedCount++;
        }

        /// <summary>
        /// Hands a character's renderers back as it returns to the pool, and forgets it,
        /// so the next NPC to reuse the object starts from a clean slate.
        /// </summary>
        private void Release(BaseCharacter character)
        {
            try
            {
                if (character == null) return;

                int id = character.GetInstanceID();
                if (!_state.ContainsKey(id)) return;

                var renderers = character.GetComponentsInChildren<Renderer>(true);
                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var renderer = renderers[i];
                        if (renderer == null) continue;

                        int rendererId = renderer.GetInstanceID();
                        if (!_originalEnabled.TryGetValue(rendererId, out bool wasEnabled)) continue;

                        renderer.enabled = wasEnabled;
                        _originalEnabled.Remove(rendererId);
                    }
                }

                if (_state[id] == State.Culled) CulledCount = Math.Max(0, CulledCount - 1);
                if (_state[id] == State.Cleaned) CleanedCount = Math.Max(0, CleanedCount - 1);

                _state.Remove(id);
            }
            catch (Exception)
            {
                // Character already torn down. Its entries go on the next full restore.
            }
        }

        /// <summary>Puts every renderer back the way it was found. Called on unload.</summary>
        internal void RestoreAll()
        {
            try
            {
                CharacterRegistry.CopyInto(_characters);

                foreach (var character in _characters)
                {
                    try
                    {
                        var renderers = character.GetComponentsInChildren<Renderer>(true);
                        if (renderers == null) continue;

                        for (int i = 0; i < renderers.Length; i++)
                        {
                            var renderer = renderers[i];
                            if (renderer == null) continue;

                            if (_originalEnabled.TryGetValue(renderer.GetInstanceID(), out bool wasEnabled))
                                renderer.enabled = wasEnabled;
                        }
                    }
                    catch (Exception) { /* gone; nothing to restore */ }
                }
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"NpcOptimizer restore failed: {ex}");
            }

            _state.Clear();
            _originalEnabled.Clear();
            CleanedCount = 0;
            CulledCount = 0;
        }

        internal void Dispose()
        {
            CharacterRegistry.Disabled -= Release;
        }
    }
}
