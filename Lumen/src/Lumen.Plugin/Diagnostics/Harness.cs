using System;
using System.Collections.Generic;
using CrazyMinnow.SALSA;
using Nivalis;
using Nivalis.Traffic;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// Owns the measurement state: frame statistics, the probe list, the selection cursor
    /// and the auto A/B reader. The overlay only draws what this holds.
    /// </summary>
    internal sealed class Harness
    {
        internal readonly FrameStats Stats = new FrameStats();
        internal readonly AutoMeasure Measure;
        internal readonly List<Probe> Probes = new List<Probe>();

        internal bool Visible;
        internal int Selected;

        /// <summary>Player-facing settings rows. The default view.</summary>
        internal readonly List<SettingRow> Settings = new List<SettingRow>();
        internal int SelectedSetting;

        /// <summary>F12 swaps the settings panel for the probe harness.</summary>
        internal bool DeveloperMode;

        /// <summary>The Unity UI renderer. Built lazily the first time it is asked for.</summary>
        internal readonly CanvasPanel Canvas = new CanvasPanel();

        /// <summary>
        /// True when the Canvas panel should be drawing. The developer harness stays on
        /// IMGUI either way - it is dense, text-heavy and read rather than browsed, which
        /// is the one job IMGUI is actually good at.
        /// </summary>
        /// <summary>
        /// Whether the Canvas panel should currently be on screen.
        /// </summary>
        internal bool UseCanvas => Visible && !DeveloperMode && Canvas.Available;

        // Live scene counts, refreshed on a timer rather than per frame. An overlay that
        // calls FindObjectsOfType every frame measures itself.
        internal int LightCount;
        internal int ShadowCastingLightCount;
        internal int CharacterCount;
        internal int TrafficCount;
        internal int SalsaCount;
        internal int VolumetricCameraCount;
        internal int PlanarReflectionCount;
        internal int PostProcessVolumeCount;

        /// <summary>
        /// Counts are refreshed on demand only. Sweeping the scene is expensive enough to
        /// show up inside a measurement window.
        /// </summary>
        internal bool CountsStale = true;

        internal Harness()
        {
            Measure = new AutoMeasure(Stats);
            Visible = LumenConfig.OverlayVisibleOnStart.Value;

            // Rows write straight through to the config entries, so a change applies on the
            // next tick and persists without a save step.
            Settings.Add(new BoolRow(LumenConfig.NpcOptimizerEnabled,
                "Crowd Performance", "Recommended") { Section = "Performance" });
            Settings.Add(new BoolRow(LumenConfig.RemoveShadowProxies,
                "Skip Unused Shadow Work", "Free extra frames"));
            Settings.Add(new CullDistanceRow(LumenConfig.NpcCullDistance));
            Settings.Add(new BoolRow(LumenConfig.ProtectNamedNpcs,
                "Protect Named NPCs", "Keeps story characters"));

            Settings.Add(new BoolRow(LumenConfig.BorderlessWindow,
                "Borderless Window", "Switch apps instantly") { Section = "Display" });

            Settings.Add(new ThemeRow(LumenConfig.Theme) { Section = "Interface" });

            // The probe list is a working set, not a catalogue. Probes.cs holds many more
            // that measured as noise on this game; adding one back is a single line.

            // The shadow proxies only matter in daylight, so the clock helper comes first.
            Probes.Add(new DaylightProbe());
            Probes.Add(new LiveShadowProxyCullProbe());


            // Visible, and off by default, but a generous distance is an acceptable trade
            // for a lot of frames.
            Probes.Add(new LiveNpcCullProbe());

            Probes.Add(new GameUiOffProbe());

            // Reference: the ceiling for anything NPC-renderer shaped.
            Probes.Add(new NpcRenderersHiddenProbe());

            // The CPU/GPU control: a large reading here means the bottleneck has moved.
            Probes.Add(new HalfResolutionProbe());
        }

        internal void Tick(float unscaledDeltaTime, bool windowFocused)
        {
            Stats.Record(unscaledDeltaTime);
            Measure.Tick(unscaledDeltaTime);

            // Live probes keep working every frame while applied.
            for (int i = 0; i < Probes.Count; i++)
            {
                var probe = Probes[i];
                if (!probe.Active) continue;

                try { probe.Tick(unscaledDeltaTime); }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogError($"Probe '{probe.Name}' tick failed, forcing restore: {ex}");
                    probe.ForceRestore();
                }
            }

            Hotkeys.Poll(windowFocused);
            HandleInput();

            UpdateCanvas();
        }

        private void UpdateCanvas()
        {
            try
            {
                if (Visible && !DeveloperMode) Canvas.Build(this);

                Canvas.SetVisible(UseCanvas);
                Canvas.Refresh(this);
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Canvas panel update failed: {ex}");
            }
        }

        private void HandleInput()
        {
            if (Hotkeys.Pressed(Hotkeys.VkF10))
                Visible = !Visible;

            if (!Visible) return;

            if (Hotkeys.Pressed(Hotkeys.VkF12) && LumenConfig.DiagnosticsEnabled.Value)
                DeveloperMode = !DeveloperMode;

            if (!DeveloperMode) { HandleSettingsInput(); return; }

            if (Hotkeys.Pressed(Hotkeys.VkF11))
                SceneReport.Write(this);

            // Sweeping the scene is expensive enough to show up in a reading, so it only
            // ever happens when asked for, and never while a reading is in flight.
            if (Hotkeys.Pressed(Hotkeys.VkF9) && !Measure.Busy)
                RefreshCounts();

            if (Hotkeys.Pressed(Hotkeys.VkUp))
                Selected = (Selected - 1 + Probes.Count) % Probes.Count;

            if (Hotkeys.Pressed(Hotkeys.VkDown))
                Selected = (Selected + 1) % Probes.Count;

            var probe = Probes[Selected];

            if (Hotkeys.Pressed(Hotkeys.VkLeft) && probe.Adjustable)
                probe.Adjust(-1);

            if (Hotkeys.Pressed(Hotkeys.VkRight) && probe.Adjustable)
                probe.Adjust(1);

            if (Hotkeys.Pressed(Hotkeys.VkReturn) && !Measure.Busy)
            {
                if (probe.Toggle())
                    Measure.Begin(probe, probe.Active);
            }

            if (Hotkeys.Pressed(Hotkeys.VkBack))
                RestoreAll();
        }

        private void HandleSettingsInput()
        {
            if (Settings.Count == 0) return;

            if (Hotkeys.Pressed(Hotkeys.VkUp))
                SelectedSetting = (SelectedSetting - 1 + Settings.Count) % Settings.Count;

            if (Hotkeys.Pressed(Hotkeys.VkDown))
                SelectedSetting = (SelectedSetting + 1) % Settings.Count;

            var row = Settings[SelectedSetting];

            try
            {
                if (Hotkeys.Pressed(Hotkeys.VkLeft)) row.Adjust(-1);
                if (Hotkeys.Pressed(Hotkeys.VkRight)) row.Adjust(1);
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Setting '{row.Label}' failed to change: {ex}");
            }
        }

        internal void RestoreAll()
        {
            Measure.Abort();

            int restored = 0;
            foreach (var probe in Probes)
            {
                if (!probe.Active) continue;
                probe.ForceRestore();
                restored++;
            }

            Measure.PushNote($"restored {restored} probe(s) to shipped values");
        }

        internal void RefreshCounts()
        {
            try
            {
                var lights = Find.All<Light>();
                LightCount = lights.Count;

                int casting = 0;
                foreach (var light in lights)
                {
                    try { if (light.shadows != LightShadows.None) casting++; }
                    catch (Exception) { /* one bad light must not lose the whole count */ }
                }
                ShadowCastingLightCount = casting;

                CharacterCount = Find.All<BaseCharacter>().Count;
                TrafficCount = Find.All<TrafficVehicle>().Count;
                SalsaCount = Find.All<Salsa>().Count;
                VolumetricCameraCount = Find.All<HxVolumetricCamera>().Count;
                PlanarReflectionCount = Find.All<PlaneReflectionScript>().Count;
                PostProcessVolumeCount = Find.All<PostProcessVolume>().Count;

                CountsStale = false;
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogWarning($"Scene count refresh failed: {ex.Message}");
            }
        }
    }
}
