using BepInEx;
using BepInEx.Configuration;

namespace Lumen
{
    /// <summary>
    /// Every setting Lumen owns.
    /// </summary>
    internal static class LumenConfig
    {
        internal static ConfigEntry<bool> NpcOptimizerEnabled;
        internal static ConfigEntry<bool> CollapseStackedLods;
        internal static ConfigEntry<bool> RemoveShadowProxies;
        internal static ConfigEntry<float> NpcCullDistance;

        internal static ConfigEntry<bool> BorderlessWindow;
        internal static ConfigEntry<string> Theme;
        internal static ConfigEntry<bool> DiagnosticsEnabled;
        internal static ConfigEntry<bool> OverlayVisibleOnStart;
        internal static ConfigEntry<float> SettleSeconds;
        internal static ConfigEntry<float> MeasureSeconds;
        internal static ConfigEntry<string> ArtifactsDir;

        internal static void Bind(ConfigFile config)
        {
            NpcOptimizerEnabled = config.Bind(
                "NPCs", "Enabled", true,
                "Master switch for the NPC renderer work. This is where essentially all of " +
                "the mod's FPS comes from.");

            CollapseStackedLods = config.Bind(
                "NPCs", "CollapseStackedLods", true,
                "Characters render every level of detail at once - the same body mesh drawn " +
                "five times over. This keeps the most detailed one and switches off the " +
                "copies underneath it. No visible change at any distance.");

            RemoveShadowProxies = config.Bind(
                "NPCs", "RemoveShadowProxies", true,
                "Each character carries extra hidden meshes whose only job is to cast a " +
                "shadow that the game's lighting never actually shows, even in daylight. " +
                "Turn this off if you ever find somewhere NPC shadows do appear.");

            NpcCullDistance = config.Bind(
                "NPCs", "CullDistance", 0f,
                new ConfigDescription(
                    "Hide NPCs beyond this many metres. 0 disables it entirely. " +
                    "This is the one setting that visibly changes the game, so it is yours " +
                    "to tune rather than something Lumen decides. " +
                    "0 = off, nothing changes. " +
                    "60 or more = hard to spot, a small gain in crowds. " +
                    "40 = some fade-in, a good gain. " +
                    "20 = obvious fade-in, the biggest gain. " +
                    "Lower means more frames and more popping. Pick the point where you " +
                    "stop noticing and leave it there.",
                    new AcceptableValueRange<float>(0f, 150f)));

            BorderlessWindow = config.Bind(
                "Display", "BorderlessWindow", true,
                "Force borderless fullscreen window. The game's own menu only offers a " +
                "Fullscreen toggle, and that gives exclusive fullscreen. Re-asserted on a " +
                "slow timer because the game resets the display mode whenever graphics " +
                "settings are applied.");

            Theme = config.Bind(
                "Interface", "Theme", "Synthwave",
                new ConfigDescription(
                    "Colour scheme for the Lumen panel.",
                    new AcceptableValueList<string>(Diagnostics.Themes.Names)));

            DiagnosticsEnabled = config.Bind(
                "Diagnostics", "DeveloperMode", true,
                "Allows F12 to open the A/B probe harness used to find optimisations. " +
                "The settings panel on F10 is unaffected either way.");

            OverlayVisibleOnStart = config.Bind(
                "Diagnostics", "VisibleOnStart", false,
                "Show the overlay as soon as the game loads. Off by default so the hotkey " +
                "is the only way in.");

            SettleSeconds = config.Bind(
                "Diagnostics", "SettleSeconds", 0.75f,
                "After a probe is flipped, how long to wait before measuring. Covers the " +
                "frame or two of churn while shaders recompile and buffers resize.");

            MeasureSeconds = config.Bind(
                "Diagnostics", "MeasureSeconds", 2.5f,
                "How long to accumulate frames for the after-reading of an A/B probe. " +
                "Longer is steadier; too long and you drift off the benchmark spot.");

            ArtifactsDir = config.Bind(
                "Diagnostics", "ArtifactsDir",
                System.IO.Path.Combine(Paths.BepInExRootPath, "Lumen"),
                "Where the scene report lands. Safe to delete.");
        }
    }
}
