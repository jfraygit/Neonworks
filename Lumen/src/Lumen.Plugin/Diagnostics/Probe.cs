using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// One reversible experiment. Flipping a probe turns a subsystem off (or down) so the
    /// frame-time delta attributes cost to it. Nothing a probe does is ever written to disk.
    /// </summary>
    internal abstract class Probe
    {
        internal abstract string Name { get; }

        /// <summary>One line on what flipping this tells you. Shown in the overlay footer.</summary>
        internal virtual string Hint => null;

        /// <summary>True when the probe is applied (the subsystem is off / turned down).</summary>
        internal bool Active { get; private set; }

        /// <summary>Short right-hand column: how many things were affected, or an error marker.</summary>
        internal string Status { get; set; }

        /// <summary>Probes that respond to left/right show an adjustable value instead of on/off.</summary>
        internal virtual bool Adjustable => false;

        internal bool Toggle()
        {
            bool restoring = Active;
            try
            {
                if (restoring) Restore(); else Apply();
                Active = !restoring;
                return true;
            }
            catch (Exception ex)
            {
                // A probe that blows up must not take the game or the harness down with it.
                LumenPlugin.Log.LogError($"Probe '{Name}' {(restoring ? "restore" : "apply")} failed: {ex}");
                Status = "ERROR";
                return false;
            }
        }

        /// <summary>Left/right on an adjustable probe. Returns true if anything changed.</summary>
        internal virtual bool Adjust(int direction) => false;

        /// <summary>
        /// Per-frame work while the probe is applied. Only live probes implement this; the
        /// cost of doing so lands inside the reading, which is correct - a fix that has to
        /// run every frame should be measured net of its own overhead.
        /// </summary>
        internal virtual void Tick(float unscaledDeltaTime) { }

        /// <summary>Put everything back, whatever state we are in. Called on unload.</summary>
        internal void ForceRestore()
        {
            if (!Active) return;
            try { Restore(); }
            catch (Exception ex) { LumenPlugin.Log.LogError($"Probe '{Name}' force-restore failed: {ex}"); }
            Active = false;
        }

        protected abstract void Apply();
        protected abstract void Restore();
    }

    /// <summary>
    /// A probe that mutates every component of one type in the loaded scenes.
    /// <para>
    /// Original values are keyed by instance ID rather than held as component references:
    /// an IL2CPP reference cached across a scene load can dangle, and dereferencing one is
    /// a crash rather than a catchable exception.
    /// </para>
    /// </summary>
    internal abstract class ComponentProbe<T, TState> : Probe where T : UnityEngine.Object
    {
        private readonly Dictionary<int, TState> _original = new Dictionary<int, TState>();

        protected abstract TState Capture(T component);
        protected abstract void Mutate(T component);
        protected abstract void Revert(T component, TState state);

        protected override void Apply()
        {
            _original.Clear();

            int affected = 0;
            var found = Find.All<T>();

            foreach (var component in found)
            {
                try
                {
                    _original[component.GetInstanceID()] = Capture(component);
                    Mutate(component);
                    affected++;
                }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogWarning($"{Name}: skipped one {typeof(T).Name}: {ex.Message}");
                }
            }

            Status = $"{affected} hit";

            if (affected == 0)
            {
                // An empty result is not the same as a free subsystem, and must not read
                // like one.
                LumenPlugin.Log.LogWarning(
                    $"{Name}: found no {typeof(T).Name} in the loaded scenes. " +
                    "This probe measured nothing - do not read its delta as zero cost.");
            }
        }

        protected override void Restore()
        {
            foreach (var component in Find.All<T>())
            {
                try
                {
                    if (_original.TryGetValue(component.GetInstanceID(), out var state))
                        Revert(component, state);
                }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogWarning($"{Name}: could not restore one {typeof(T).Name}: {ex.Message}");
                }
            }

            _original.Clear();
            Status = null;
        }
    }

    internal static class Find
    {
        /// <summary>
        /// Every live component of a type. Returns an empty list rather than throwing, so a
        /// type that got stripped or renamed by a game patch degrades into "measured nothing"
        /// instead of taking the overlay down.
        /// </summary>
        internal static List<T> All<T>() where T : UnityEngine.Object
        {
            var result = new List<T>();

            try
            {
                var found = UnityEngine.Object.FindObjectsOfType<T>();
                if (found == null) return result;

                for (int i = 0; i < found.Length; i++)
                {
                    var item = found[i];
                    if (item != null) result.Add(item);
                }
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogWarning($"FindObjectsOfType<{typeof(T).Name}> failed: {ex.Message}");
            }

            return result;
        }
    }
}
