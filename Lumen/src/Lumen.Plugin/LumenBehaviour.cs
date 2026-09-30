using System;
using Lumen.Diagnostics;
using UnityEngine;

namespace Lumen
{
    /// <summary>
    /// The one injected MonoBehaviour. Deliberately thin: only Unity message methods live
    /// here, so Il2CppInterop's class injector has nothing awkward to marshal. Everything
    /// else is plain managed code in <see cref="Harness"/>.
    /// </summary>
    public sealed class LumenBehaviour : MonoBehaviour
    {
        // Required by Il2CppInterop for injected types.
        public LumenBehaviour(IntPtr pointer) : base(pointer) { }

        internal static Harness Harness;
        internal static Tuning.NpcOptimizer Optimizer;

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;

            // Display mode is independent of the measurement harness and runs even when
            // diagnostics are switched off in config.
            try
            {
                DisplayMode.Tick(dt);
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Lumen display tick failed: {ex}");
            }

            try
            {
                Optimizer?.Tick(dt);
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Lumen optimizer tick failed: {ex}");
            }

            if (Harness == null) return;

            try
            {
                Harness.Tick(dt, Application.isFocused);
            }
            catch (Exception ex)
            {
                // An exception escaping into IL2CPP's native call stack is a crash, not a
                // stack trace. Log once per failure and keep the game running.
                LumenPlugin.Log.LogError($"Lumen Update failed: {ex}");
            }
        }

        private void OnGUI()
        {
            if (Harness == null) return;

            try
            {
                Overlay.Draw(Harness);
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Lumen OnGUI failed: {ex}");
            }
        }

        private void OnDestroy()
        {
            try
            {
                Harness?.RestoreAll();
                Harness?.Canvas.Destroy();
                Optimizer?.RestoreAll();
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Lumen teardown failed: {ex}");
            }
        }
    }
}
