using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Lumen.Diagnostics;
using UnityEngine;

namespace Lumen
{
    /// <summary>
    /// Lumen: a performance mod for Nivalis Nights.
    /// </summary>
    [BepInPlugin(Guid, "Lumen", Version)]
    public class LumenPlugin : BasePlugin
    {
        public const string Guid = "dev.lumen.nivalis";
        public const string Version = "0.1.0";

        // Static shadow of BasePlugin.Log so every file can reach the logger without a
        // plugin instance. Intentional; `new` only silences the hiding warning.
        internal static new ManualLogSource Log;

        private GameObject _host;

        public override void Load()
        {
            Log = base.Log;

            Log.LogInfo($"Lumen {Version} loading");
            Log.LogInfo($"  Unity    : {Application.unityVersion}");
            Log.LogInfo($"  Renderer : {SystemInfo.graphicsDeviceName}");
            Log.LogInfo($"  API      : {SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceVersion})");
            Log.LogInfo($"  VRAM     : {SystemInfo.graphicsMemorySize} MB");
            Log.LogInfo($"  CPU      : {SystemInfo.processorType} ({SystemInfo.processorCount} threads)");
            Log.LogInfo($"  RAM      : {SystemInfo.systemMemorySize} MB");

            LumenConfig.Bind(Config);

            try
            {
                var harmony = new Harmony(Guid);
                CharacterRegistry.Install(harmony);
                DisplayMode.Install(harmony);
            }
            catch (Exception ex)
            {
                // Without the registry the live probes fall back to scene sweeps, which is
                // slower and noisier but still works.
                Log.LogError($"Harmony setup failed: {ex}");
            }

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<LumenBehaviour>();

                _host = new GameObject("Lumen");
                _host.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_host);

                LumenBehaviour.Optimizer = new Tuning.NpcOptimizer();

                // The harness owns the settings panel as well as the probes, so it is
                // always built. Only F12 and the probe harness answer to the config.
                LumenBehaviour.Harness = new Harness();

                _host.AddComponent<LumenBehaviour>();

                Log.LogInfo(LumenConfig.DiagnosticsEnabled.Value
                    ? "Ready. F10 for settings, F12 for the developer harness."
                    : "Ready. F10 for settings.");
            }
            catch (Exception ex)
            {
                // A failed harness must never take the game down with it.
                Log.LogError($"Failed to stand up the Lumen host: {ex}");
            }
        }

        public override bool Unload()
        {
            try
            {
                LumenBehaviour.Harness?.RestoreAll();
                LumenBehaviour.Harness = null;

                LumenBehaviour.Optimizer?.RestoreAll();
                LumenBehaviour.Optimizer?.Dispose();
                LumenBehaviour.Optimizer = null;

                if (_host != null) UnityEngine.Object.Destroy(_host);
            }
            catch (Exception ex)
            {
                Log.LogError($"Lumen unload failed: {ex}");
            }

            return true;
        }
    }
}
