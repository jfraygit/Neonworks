using System;
using System.Collections.Generic;
using Nivalis;
using UnityEngine;

namespace Lumen.Tuning
{
    /// <summary>
    /// Removes redundant NPC renderer work. Always on, config driven.
    /// <para>
    /// Characters carry dedicated skinned meshes whose only job is to cast a shadow the
    /// scene's lighting never shows. Those are removed, and optionally whole characters
    /// past a distance the player chooses.
    /// </para>
    /// <para>
    /// It happens in a single pass spread across frames. Separate passes would each pay
    /// their own traversal cost, which is large enough to cancel out the saving.
    /// </para>
    /// </summary>
    internal sealed class NpcOptimizer
    {
        private enum State { Untouched, Cleaned, Culled }

        private const float RefreshInterval = 0.5f;
        private const float Hysteresis = 0.9f;

        private readonly List<BaseCharacter> _characters = new List<BaseCharacter>();
        private readonly Dictionary<int, State> _state = new Dictionary<int, State>();
        // Renderers Lumen switched off, and nothing else.
        //
        // This used to record the state it found, including "was already off", and restore
        // that. That is wrong: the game's own LODGroup keeps choosing levels while a
        // character is hidden, so a renderer noted as off can legitimately be on by the time
        // the note is applied - and restoring it to off kills a body the game had just
        // enabled. It showed up as a character's hair floating with nothing under it.
        //
        // Only ever turning renderers back ON removes the whole class of problem: Lumen
        // cannot suppress something it did not suppress itself.
        private readonly HashSet<int> _hidden = new HashSet<int>();
        private readonly HashSet<int> _redundant = new HashSet<int>();

        private float _sinceRefresh = RefreshInterval;
        private int _cursor;

        // Last applied option values, so a change can be noticed and acted on.
        private bool _optionsKnown;
        private bool _dropShadows;

        /// <summary>True while any renderer is overridden and owes a restore.</summary>
        private bool HasOverrides => _state.Count > 0 || _hidden.Count > 0;

        internal NpcOptimizer()
        {
            CharacterRegistry.Disabled += Release;
        }

        internal int CleanedCount { get; private set; }
        internal int CulledCount { get; private set; }

        internal void Tick(float unscaledDeltaTime)
        {
            // Switching off has to put everything back, not merely stop working.
            //
            // This used to be a bare `return`, which left every renderer that had already
            // been hidden hidden until the game restarted - while the panel told the player
            // that turning it off restores the game immediately. The off switch is the
            // escape hatch for anything going wrong, so it has to be the most reliable part
            // of the whole mod.
            if (!LumenConfig.NpcOptimizerEnabled.Value)
            {
                if (HasOverrides) RestoreAll();
                return;
            }

            // A change to either cleanup option has to revisit characters that were already
            // dealt with. The per-character state machine only re-evaluates on a transition,
            // so without this a character marked clean keeps the previous decision for the
            // rest of the session and the setting appears to do nothing.
            bool shadows = LumenConfig.RemoveShadowProxies.Value;

            if (_optionsKnown && shadows != _dropShadows) RestoreAll();

            _dropShadows = shadows;
            _optionsKnown = true;

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

            ReportCullDiagnostics(camera, eye, unscaledDeltaTime);

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
                    // Already off - leave it alone. Whatever turned it off owns it.
                    if (!renderer.enabled) continue;

                    renderer.enabled = false;
                    _hidden.Add(rendererId);
                }
                else if (_hidden.Remove(rendererId))
                {
                    renderer.enabled = true;
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

                        if (!_hidden.Remove(renderer.GetInstanceID())) continue;

                        renderer.enabled = true;
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


        // ------------------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------------------

        private float _sinceReport;
        private int _reportsLeft = 6;

        /// <summary>
        /// Periodically records which camera distances are measured from and how close the
        /// nearest culled character is.
        /// <para>
        /// <c>Camera.main</c> returns the first enabled camera tagged MainCamera, and this
        /// scene has five cameras. If it ever resolves to one that is not where the player
        /// is, every distance is wrong and characters standing in front of you measure as
        /// far away. This says so outright instead of leaving it to be inferred.
        /// </para>
        /// </summary>
        private void ReportCullDiagnostics(Camera camera, Vector3 eye, float unscaledDeltaTime)
        {
            if (_reportsLeft <= 0) return;
            if (LumenConfig.NpcCullDistance.Value <= 0f) return;

            _sinceReport += unscaledDeltaTime;
            if (_sinceReport < 5f) return;
            _sinceReport = 0f;
            _reportsLeft--;

            try
            {
                float nearestCulled = float.MaxValue;
                int culled = 0;

                foreach (var character in _characters)
                {
                    try
                    {
                        if (!_state.TryGetValue(character.GetInstanceID(), out State state)) continue;
                        if (state != State.Culled) continue;

                        culled++;
                        float distance = Vector3.Distance(character.transform.position, eye);
                        if (distance < nearestCulled) nearestCulled = distance;
                    }
                    catch (Exception) { }
                }

                LumenPlugin.Log.LogInfo(
                    $"[cull] camera='{camera.name}' at {eye.x:0},{eye.y:0},{eye.z:0}  " +
                    $"limit={LumenConfig.NpcCullDistance.Value:0}m  " +
                    $"characters={_characters.Count} culled={culled}  " +
                    $"nearest culled={(culled == 0 ? "n/a" : nearestCulled.ToString("0.0") + "m")}");
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogWarning($"Cull diagnostics failed: {ex.Message}");
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

                            if (_hidden.Remove(renderer.GetInstanceID()))
                                renderer.enabled = true;
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
            _hidden.Clear();
            CleanedCount = 0;
            CulledCount = 0;
        }

        internal void Dispose()
        {
            CharacterRegistry.Disabled -= Release;
        }
    }
}
