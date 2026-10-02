using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace Nightshare
{
    [BepInPlugin(Guid, "Nightshare", Version)]
    public class NightsharePlugin : BasePlugin
    {
        public const string Guid = "dev.nightshare.nivalis";

        /// <summary>
        /// Sent in the handshake and refused on mismatch, so both players must run the
        /// same build. Keep in step with NightshareVersion in Directory.Build.props.
        /// </summary>
        public const string Version = "0.1.0";

        internal static ManualLogSource Logger;
        internal static NightshareRunner Runner;

        /// <summary>
        /// Our settings. Named Settings rather than Config because BasePlugin already has
        /// a Config of its own (the raw ConfigFile) and shadowing it reads as a bug later.
        /// </summary>
        internal static NightshareConfig Settings;

        /// <summary>
        /// Probe output, traces and desync reports. Under the BepInEx root so it works on
        /// any install. The exact path is logged on load.
        /// </summary>
        internal static readonly string ArtifactsDir =
            Path.Combine(Paths.BepInExRootPath, "Nightshare-artifacts");

        public override void Load()
        {
            Logger = Log;
            Settings = new NightshareConfig(base.Config);

            Log.LogInfo($"Nightshare {Version} loading");
            Log.LogInfo($"  Unity      : {UnityEngine.Application.unityVersion}");
            Log.LogInfo($"  Product    : {UnityEngine.Application.productName}");
            Log.LogInfo($"  Game build : {GameFingerprint.Current}");
            Log.LogInfo($"  Persistent : {UnityEngine.Application.persistentDataPath}");

            if (GameFingerprint.IsUnknown)
            {
                Log.LogWarning("Could not fingerprint the game build. Every join will be " +
                               "refused, because a version mismatch corrupts state rather " +
                               "than failing cleanly.");
            }

            try
            {
                Directory.CreateDirectory(ArtifactsDir);
                Log.LogInfo($"  Artifacts  : {ArtifactsDir}");

                // Per-process log. Two instances share BepInEx's LogOutput.log and only
                // the first can hold it open, so without this the second instance's log
                // is lost entirely.
                NightshareLog.Open(ArtifactsDir, CommandLineOverrides.Mode?.ToString() ?? "game");
                if (NightshareLog.Path != null)
                    Log.LogInfo($"  Log        : {NightshareLog.Path}");

                // Capture the game's own output too, so a second instance's warnings and
                // exceptions are not lost to the shared BepInEx log.
                NightshareLog.MirrorUnityLog();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not create the artifacts directory: {ex.Message}");
            }

            try
            {
                var harmony = new Harmony(Guid);
                harmony.PatchAll(typeof(NightsharePlugin).Assembly);
                Log.LogInfo($"Harmony patched {harmony.GetPatchedMethods().Count()} method(s)");
            }
            catch (Exception ex)
            {
                // A failed patch must never take the game down with it.
                Log.LogError($"Harmony PatchAll failed: {ex}");
            }

            try
            {
                Runner = AddComponent<NightshareRunner>();
                Log.LogInfo("Runner attached");
            }
            catch (Exception ex)
            {
                Log.LogError($"Could not attach the runner, Nightshare is inert: {ex}");
                return;
            }

            Log.LogInfo($"Ready. {Settings.HostKey.Value} to host, " +
                        $"{Settings.JoinKey.Value} to join {Settings.Endpoint.Value}, " +
                        $"{Settings.LeaveKey.Value} to leave, {Settings.StatusKey.Value} for status.");
        }

        public override bool Unload()
        {
            try { NightshareCore.Instance.Leave(); } catch { }
            return base.Unload();
        }
    }
}
