using System;
using System.Collections.Generic;
using Nivalis;
using UnityEngine;

namespace Lumen.Tuning
{
    /// <summary>
    /// Applies optional renderer filters and camera-distance culling in slices of the crowd.
    /// Renderer names alone do not prove redundant drawing or invisible shadows.
    /// Original enabled values remain owned until restoration succeeds or the target is gone.
    /// </summary>
    internal sealed class NpcOptimizer
    {
        private enum State { Untouched, Cleaned, Culled }

        private const float RefreshInterval = 0.5f;
        private const float Hysteresis = 0.9f;

        private readonly List<BaseCharacter> _characters = new List<BaseCharacter>();
        private readonly Dictionary<int, State> _state = new Dictionary<int, State>();
        private readonly Dictionary<int, BaseCharacter> _owners = new Dictionary<int, BaseCharacter>();
        private readonly RendererEnabledLedger _enabled = new RendererEnabledLedger();
        private readonly HashSet<int> _redundant = new HashSet<int>();
        private readonly HashSet<int> _registered = new HashSet<int>();

        private float _sinceRefresh = RefreshInterval;
        private int _cursor;
        private bool _optionsKnown, _collapseLods, _dropShadows;
        private bool _restoreAllRequested, _restoreWarning;

        internal NpcOptimizer()
        {
            CharacterRegistry.Disabled += Release;
        }

        internal int CleanedCount { get; private set; }
        internal int CulledCount { get; private set; }
        internal int PendingRestoreCount => _enabled.PendingRestoreCount;

        internal void Tick(float unscaledDeltaTime)
        {
            // Switching off must work even while there is no camera or live registry.
            if (!LumenConfig.NpcOptimizerEnabled.Value)
            {
                RestoreAll();
                return;
            }
            if (_restoreAllRequested)
            {
                RestoreAll();
                if (_restoreAllRequested) return;
            }

            bool collapse = LumenConfig.CollapseStackedLods.Value;
            bool shadows = LumenConfig.RemoveShadowProxies.Value;
            if (_optionsKnown && (collapse != _collapseLods || shadows != _dropShadows))
            {
                // Revisit already-Cleaned characters as well as future state transitions.
                RestoreAll();
                if (_restoreAllRequested) return;
            }
            _collapseLods = collapse;
            _dropShadows = shadows;
            if (!_optionsKnown)
                LumenPlugin.Log.LogInfo($"NPC optimizer active: collapseLods={collapse}, removeShadowProxies={shadows}, cullDistance={LumenConfig.NpcCullDistance.Value}.");
            _optionsKnown = true;

            var camera = Camera.main;
            if (camera == null) return;

            _sinceRefresh += unscaledDeltaTime;
            if (_sinceRefresh >= RefreshInterval)
            {
                _sinceRefresh = 0f;
                CharacterRegistry.CopyInto(_characters);
                _cursor = 0;
                _registered.Clear();
                foreach (var character in _characters)
                {
                    try { if (character != null && character.isActiveAndEnabled) _registered.Add(character.GetInstanceID()); }
                    catch (Exception) { }
                }
                foreach (int id in new List<int>(_owners.Keys))
                    if (!_registered.Contains(id)) Release(id);
                if (_restoreAllRequested) return;
            }

            if (_characters.Count == 0) return;
            Vector3 eye = camera.transform.position;
            float cullDistance = LumenConfig.NpcCullDistance.Value;
            bool cullEnabled = cullDistance > 0f;
            float farSq = cullDistance * cullDistance;
            float nearSq = cullDistance * Hysteresis * cullDistance * Hysteresis;

            // An eighth of the crowd per frame; elapsed revisit time depends on frame rate.
            int slice = Math.Max(8, _characters.Count / 8);
            for (int n = 0; n < slice; n++)
            {
                if (_characters.Count == 0 || _restoreAllRequested) return;
                if (_cursor >= _characters.Count) _cursor = 0;
                var character = _characters[_cursor++];
                int? ownerId = null;
                try
                {
                    if (character == null || !character.isActiveAndEnabled) continue;
                    int id = character.GetInstanceID();
                    ownerId = id;
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
                    // Apply can fail after a setter already changed a renderer. Its stored
                    // target must be restored even when no completed state was recorded.
                    if (ownerId.HasValue) Release(ownerId.Value);
                    int index = Math.Max(0, _cursor - 1);
                    if (index < _characters.Count) _characters.RemoveAt(index);
                    _cursor = index;
                }
            }
        }

        private void ApplyState(BaseCharacter character, int id, State from, State to)
        {
            _owners[id] = character;
            // A culled renderer may have been detached since acquisition. Restore the retained
            // targets before filtering the current hierarchy, not just those found below it now.
            if (to == State.Cleaned)
            {
                if (!_enabled.RestoreOwner(id)) throw new InvalidOperationException("Renderer restoration remains pending.");
                RequireOwner(character, id);
            }
            var renderers = character.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return;

            if (to == State.Culled) _redundant.Clear();
            else CharacterRenderers.FindRedundant(renderers, _redundant, _collapseLods, _dropShadows);

            for (int i = 0; i < renderers.Length; i++)
            {
                RequireOwner(character, id);
                var renderer = renderers[i];
                if (renderer == null) continue;
                int rendererId = renderer.GetInstanceID();
                bool shouldHide = to == State.Culled || _redundant.Contains(rendererId);
                if (shouldHide) _enabled.Hide(id, new RendererTarget(renderer, rendererId));
                else if (!_enabled.RestoreRenderer(rendererId))
                    throw new InvalidOperationException("Renderer restoration remains pending.");
            }
            RequireOwner(character, id);
            _state[id] = to;
            if (from == State.Culled) CulledCount = Math.Max(0, CulledCount - 1);
            if (from == State.Cleaned) CleanedCount = Math.Max(0, CleanedCount - 1);
            if (to == State.Culled) CulledCount++;
            if (to == State.Cleaned) CleanedCount++;
        }

        private void RequireOwner(BaseCharacter character, int id)
        {
            if (_restoreAllRequested || !_owners.TryGetValue(id, out var owner) || !ReferenceEquals(owner, character))
                throw new InvalidOperationException("Character was released during the renderer update.");
        }

        private void Release(BaseCharacter character)
        {
            try
            {
                if (ReferenceEquals(character, null)) return;
                Release(character.GetInstanceID());
            }
            catch (Exception)
            {
                // The owner's wrapper is gone; retained renderer references still allow recovery.
                RestoreAll();
            }
        }

        private void Release(int id)
        {
            // Partial Apply registers its owner before the first setter. Unrelated disable
            // callbacks must not scan/copy the full renderer ledger.
            if (!_owners.ContainsKey(id)) return;
            if (!_enabled.RestoreOwner(id))
            {
                _restoreAllRequested = true;
                WarnPending();
                return;
            }
            if (_state.TryGetValue(id, out var state))
            {
                if (state == State.Culled) CulledCount = Math.Max(0, CulledCount - 1);
                if (state == State.Cleaned) CleanedCount = Math.Max(0, CleanedCount - 1);
                _state.Remove(id);
            }
            _owners.Remove(id);
        }

        /// <summary>Restore retained renderer references, including absent/partially processed owners.</summary>
        internal void RestoreAll()
        {
            int trackedBefore = _enabled.Count;
            _restoreAllRequested = true;
            if (!_enabled.RestoreAll())
            {
                WarnPending();
                return;
            }
            _state.Clear();
            _owners.Clear();
            _characters.Clear();
            _cursor = 0;
            _sinceRefresh = RefreshInterval;
            _optionsKnown = false;
            _restoreAllRequested = false;
            _restoreWarning = false;
            CleanedCount = 0;
            CulledCount = 0;
            if (trackedBefore > 0)
                LumenPlugin.Log.LogInfo($"NPC renderer restoration completed: {trackedBefore} tracked state(s) processed, 0 remaining.");
        }

        private void WarnPending()
        {
            if (_restoreWarning) return;
            _restoreWarning = true;
            LumenPlugin.Log.LogWarning($"NPC renderer restoration is pending for {PendingRestoreCount} renderer(s); changes are paused and restoration will retry.");
        }

        internal void Dispose()
        {
            if (PendingRestoreCount == 0) CharacterRegistry.Disabled -= Release;
        }

        private sealed class RendererTarget : IRendererEnabledTarget
        {
            private readonly Renderer _renderer;
            public int Id { get; }
            internal RendererTarget(Renderer renderer, int id) { _renderer = renderer; Id = id; }
            public bool IsAlive => _renderer != null && _renderer.GetInstanceID() == Id;
            public bool Enabled { get => _renderer.enabled; set => _renderer.enabled = value; }
        }
    }
}
