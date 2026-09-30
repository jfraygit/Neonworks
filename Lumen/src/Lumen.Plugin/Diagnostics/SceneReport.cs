using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Nivalis;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// One-shot census of the loaded scenes, written to the artifacts directory. Answers
    /// the questions a frame-time probe cannot: which layers, shaders and renderers actually
    /// dominate, and what the character rigs are built from.
    /// </summary>
    internal static class SceneReport
    {
        internal static void Write(Harness harness)
        {
            try
            {
                string dir = LumenConfig.ArtifactsDir.Value;
                Directory.CreateDirectory(dir);

                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string path = Path.Combine(dir, $"scene-report_{stamp}.txt");

                var text = new StringBuilder();
                AppendScenes(text);
                AppendLayers(text);
                AppendCameras(text);
                AppendLights(text);
                AppendRenderers(text);
                AppendVolumetrics(text);
                AppendPlanarReflections(text);
                AppendCharacters(text, harness);

                File.WriteAllText(path, text.ToString());

                harness.Measure.PushNote($"scene report -> {path}");
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Scene report failed: {ex}");
                harness.Measure.PushNote("scene report FAILED - see LogOutput.log");
            }
        }

        private static void AppendScenes(StringBuilder text)
        {
            text.AppendLine("=== Scenes ===");
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    text.AppendLine($"  [{scene.buildIndex}] {scene.name}  loaded={scene.isLoaded}");
                }
            }
            catch (Exception ex) { text.AppendLine($"  failed: {ex.Message}"); }
            text.AppendLine();
        }

        private static void AppendLayers(StringBuilder text)
        {
            text.AppendLine("=== Layers ===");
            text.AppendLine("  Named layers only. Never hardcode a layer index without checking here;");
            text.AppendLine("  they are project-specific and change between builds.");
            for (int i = 0; i < 32; i++)
            {
                string name;
                try { name = LayerMask.LayerToName(i); }
                catch (Exception) { name = null; }

                if (!string.IsNullOrEmpty(name))
                    text.AppendLine($"  {i,2}  {name}");
            }
            text.AppendLine();
        }

        private static void AppendCameras(StringBuilder text)
        {
            text.AppendLine("=== Cameras ===");
            foreach (var camera in Find.All<Camera>())
            {
                try
                {
                    text.AppendLine($"  {camera.name}  enabled={camera.enabled}  depth={camera.depth}  " +
                                    $"far={camera.farClipPlane:0}  path={camera.renderingPath}  " +
                                    $"hdr={camera.allowHDR}  msaa={camera.allowMSAA}");

                    var distances = camera.layerCullDistances;
                    if (distances == null) continue;

                    bool any = false;
                    for (int i = 0; i < distances.Length; i++)
                    {
                        if (distances[i] <= 0f) continue;
                        string layerName = LayerMask.LayerToName(i);
                        text.AppendLine($"      cull layer {i,2} ({layerName}) = {distances[i]:0}");
                        any = true;
                    }
                    if (!any) text.AppendLine("      no per-layer cull distances set (all use the far plane)");
                }
                catch (Exception ex) { text.AppendLine($"  camera failed: {ex.Message}"); }
            }
            text.AppendLine();
        }

        private static void AppendLights(StringBuilder text)
        {
            text.AppendLine("=== Lights ===");

            var byType = new Dictionary<string, int>();
            var castingByType = new Dictionary<string, int>();
            int total = 0;

            foreach (var light in Find.All<Light>())
            {
                try
                {
                    string key = $"{light.type}/{light.renderMode}";
                    byType.TryGetValue(key, out int n);
                    byType[key] = n + 1;

                    if (light.shadows != LightShadows.None)
                    {
                        castingByType.TryGetValue(key, out int c);
                        castingByType[key] = c + 1;
                    }

                    total++;
                }
                catch (Exception) { /* one bad light must not lose the census */ }
            }

            // Which layers the shadow-casting lights actually see. If the Character layer
            // is outside the directional's culling mask, every *_Shadow renderer on every
            // NPC is skinned each frame for nothing.
            foreach (var light in Find.All<Light>())
            {
                try
                {
                    if (light.shadows == LightShadows.None) continue;

                    text.AppendLine($"  SHADOW CASTER: {light.name} ({light.type})  " +
                                    $"strength={light.shadowStrength:0.00}  bias={light.shadowBias:0.000}");

                    int mask = light.cullingMask;
                    text.Append("    culling mask covers:");
                    for (int i = 0; i < 32; i++)
                    {
                        if ((mask & (1 << i)) == 0) continue;
                        string layerName = LayerMask.LayerToName(i);
                        if (!string.IsNullOrEmpty(layerName)) text.Append($" {layerName}");
                    }
                    text.AppendLine();

                    int characterLayer = LayerMask.NameToLayer("Character");
                    bool seesCharacters = characterLayer >= 0 && (mask & (1 << characterLayer)) != 0;
                    text.AppendLine($"    sees the Character layer: {seesCharacters}");
                }
                catch (Exception ex) { text.AppendLine($"    failed: {ex.Message}"); }
            }

            text.AppendLine($"  total {total}");
            foreach (var pair in byType)
            {
                castingByType.TryGetValue(pair.Key, out int casting);
                text.AppendLine($"  {pair.Key,-28} {pair.Value,5}   shadow-casting {casting}");
            }
            text.AppendLine();
        }

        private static void AppendRenderers(StringBuilder text)
        {
            text.AppendLine("=== Renderers ===");

            var byLayer = new Dictionary<int, int>();
            var byShader = new Dictionary<string, int>();
            int total = 0;
            int withoutLod = 0;

            foreach (var renderer in Find.All<Renderer>())
            {
                try
                {
                    total++;

                    int layer = renderer.gameObject.layer;
                    byLayer.TryGetValue(layer, out int n);
                    byLayer[layer] = n + 1;

                    if (renderer.GetComponentInParent<LODGroup>() == null) withoutLod++;

                    var materials = renderer.sharedMaterials;
                    if (materials == null) continue;

                    for (int i = 0; i < materials.Length; i++)
                    {
                        var material = materials[i];
                        if (material == null || material.shader == null) continue;

                        string shader = material.shader.name;
                        byShader.TryGetValue(shader, out int s);
                        byShader[shader] = s + 1;
                    }
                }
                catch (Exception) { /* skip and keep counting */ }
            }

            text.AppendLine($"  total {total}   without any LODGroup {withoutLod}");
            text.AppendLine();
            text.AppendLine("  by layer (cull candidates are the small-prop layers, never buildings or characters):");

            foreach (var pair in Sorted(byLayer))
            {
                string name;
                try { name = LayerMask.LayerToName(pair.Key); }
                catch (Exception) { name = "?"; }
                text.AppendLine($"    {pair.Key,2} {name,-24} {pair.Value,6}");
            }

            text.AppendLine();
            text.AppendLine("  top shaders by renderer-material count:");
            int shown = 0;
            foreach (var pair in SortedByValue(byShader))
            {
                text.AppendLine($"    {pair.Value,6}  {pair.Key}");
                if (++shown >= 30) break;
            }
            text.AppendLine();
        }

        private static void AppendVolumetrics(StringBuilder text)
        {
            text.AppendLine("=== HxVolumetricCamera ===");
            foreach (var camera in Find.All<HxVolumetricCamera>())
            {
                try
                {
                    text.AppendLine($"  {camera.name}  enabled={camera.enabled}");
                    text.AppendLine($"    resolution={camera.resolution}  SampleCount={camera.SampleCount}  " +
                                    $"DirectionalSampleCount={camera.DirectionalSampleCount}");
                    text.AppendLine($"    MaxLightDistance={camera.MaxLightDistance:0}  " +
                                    $"MaxDirectionalRayDistance={camera.MaxDirectionalRayDistance:0}  " +
                                    $"Density={camera.Density:0.00}");
                    text.AppendLine($"    TemporalSampling={camera.TemporalSampling}  " +
                                    $"NoiseEnabled={camera.NoiseEnabled}  " +
                                    $"blurCount={camera.blurCount}  UpSampledblurCount={camera.UpSampledblurCount}");
                    text.AppendLine($"    TransparencySupport={camera.TransparencySupport}  " +
                                    $"ParticleDensitySupport={camera.ParticleDensitySupport}  " +
                                    $"densityResolution={camera.densityResolution}");
                }
                catch (Exception ex) { text.AppendLine($"  failed: {ex.Message}"); }
            }
            text.AppendLine();
        }

        private static void AppendPlanarReflections(StringBuilder text)
        {
            text.AppendLine("=== PlaneReflectionScript ===");
            text.AppendLine("  Each enabled instance is a full extra scene render.");
            foreach (var reflection in Find.All<PlaneReflectionScript>())
            {
                try
                {
                    text.AppendLine($"  {reflection.name}  enabled={reflection.enabled}  " +
                                    $"size={reflection.reflectionMapSize}  renderShadows={reflection.renderShadows}  " +
                                    $"shadowDistance={reflection.shadowDistance:0}  " +
                                    $"maxPixelLights={reflection.maxPixelLights}  " +
                                    $"far={reflection.farPlaneDistance:0}  path={reflection.renderingPath}");
                }
                catch (Exception ex) { text.AppendLine($"  failed: {ex.Message}"); }
            }
            text.AppendLine();
        }

        private static void AppendCharacters(StringBuilder text, Harness harness)
        {
            harness.RefreshCounts();

            text.AppendLine("=== Characters and traffic ===");
            text.AppendLine($"  BaseCharacter      {harness.CharacterCount}");
            text.AppendLine($"  TrafficVehicle     {harness.TrafficCount}");
            text.AppendLine($"  Salsa (lip-sync)   {harness.SalsaCount}");
            text.AppendLine($"  CharacterIK        {Find.All<CharacterIK>().Count}");
            text.AppendLine();

            AppendSkinning(text, harness);
            AppendAnimators(text);
            AppendReflectionProbes(text);

            text.AppendLine("=== QualitySettings ===");
            try
            {
                int level = QualitySettings.GetQualityLevel();
                var names = QualitySettings.names;
                string levelName = (names != null && level >= 0 && level < names.Length)
                    ? names[level]
                    : "?";

                text.Append("  presets:");
                if (names != null)
                    for (int i = 0; i < names.Length; i++)
                        text.Append($" [{i}]{names[i]}");
                text.AppendLine();

                text.AppendLine($"  ACTIVE LEVEL = {level} ({levelName})");
                text.AppendLine($"  shadows={QualitySettings.shadows}  " +
                                $"cascades={QualitySettings.shadowCascades}  projection={QualitySettings.shadowProjection}");
                text.AppendLine($"  shadowDistance={QualitySettings.shadowDistance:0}  " +
                                $"shadowResolution={QualitySettings.shadowResolution}  " +
                                $"lodBias={QualitySettings.lodBias:0.00}  maximumLODLevel={QualitySettings.maximumLODLevel}");
                text.AppendLine($"  masterTextureLimit={QualitySettings.masterTextureLimit}  " +
                                $"anisotropic={QualitySettings.anisotropicFiltering}  " +
                                $"pixelLightCount={QualitySettings.pixelLightCount}");
                text.AppendLine($"  vSyncCount={QualitySettings.vSyncCount}  " +
                                $"targetFrameRate={Application.targetFrameRate}  " +
                                $"realtimeReflectionProbes={QualitySettings.realtimeReflectionProbes}  " +
                                $"particleRaycastBudget={QualitySettings.particleRaycastBudget}");
            }
            catch (Exception ex) { text.AppendLine($"  failed: {ex.Message}"); }
            text.AppendLine();
        }

        /// <summary>
        /// Where the NPC draw cost actually lives. ~240 characters carrying ~2,000
        /// renderers means a modular avatar rig, and the parts-per-character figure says
        /// whether culling accessory parts at distance is worth building.
        /// </summary>
        private static void AppendSkinning(StringBuilder text, Harness harness)
        {
            text.AppendLine("=== Skinning ===");

            try
            {
                text.AppendLine($"  QualitySettings.skinWeights = {QualitySettings.skinWeights}");

                var skinned = Find.All<SkinnedMeshRenderer>();
                int updateOffscreen = 0;
                long bones = 0;

                foreach (var renderer in skinned)
                {
                    try
                    {
                        if (renderer.updateWhenOffscreen) updateOffscreen++;
                        var rig = renderer.bones;
                        if (rig != null) bones += rig.Length;
                    }
                    catch (Exception) { /* skip and keep counting */ }
                }

                text.AppendLine($"  SkinnedMeshRenderer total           {skinned.Count}");
                text.AppendLine($"  ...with updateWhenOffscreen set     {updateOffscreen}");
                text.AppendLine($"  total bones across all of them      {bones}");

                int characters = harness.CharacterCount;
                if (characters > 0)
                {
                    var parts = new Dictionary<int, int>();
                    foreach (var character in Find.All<BaseCharacter>())
                    {
                        try
                        {
                            var renderers = character.GetComponentsInChildren<Renderer>(true);
                            int n = renderers == null ? 0 : renderers.Length;
                            parts.TryGetValue(n, out int count);
                            parts[n] = count + 1;
                        }
                        catch (Exception) { /* skip */ }
                    }

                    text.AppendLine();
                    text.AppendLine("  renderers per character (draw calls per NPC):");
                    foreach (var pair in Sorted(parts))
                        text.AppendLine($"    {pair.Key,3} renderers  x {pair.Value} characters");

                    AppendLodStackSummary(text);
                    AppendCharacterBreakdown(text);
                }
            }
            catch (Exception ex) { text.AppendLine($"  failed: {ex.Message}"); }

            text.AppendLine();
        }

        /// <summary>
        /// How many characters are drawing every LOD level at once, counted across the
        /// whole crowd rather than sampled.
        /// </summary>
        private static void AppendLodStackSummary(StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("  stacked LOD check (how many LOD levels are ON per part):");

            var histogram = new Dictionary<int, int>();
            int charactersWithStacks = 0;
            int redundantRenderers = 0;

            foreach (var character in Find.All<BaseCharacter>())
            {
                try
                {
                    var renderers = character.GetComponentsInChildren<Renderer>(true);
                    if (renderers == null) continue;

                    // group name -> how many of its LOD levels are currently enabled
                    var activePerGroup = new Dictionary<string, int>();

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var renderer = renderers[i];
                        if (renderer == null) continue;
                        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;

                        string name = renderer.name;
                        int marker = name.LastIndexOf("_LOD_", StringComparison.OrdinalIgnoreCase);
                        if (marker < 0) continue;

                        string group = name.Substring(0, marker).ToLowerInvariant();
                        activePerGroup.TryGetValue(group, out int n);
                        activePerGroup[group] = n + 1;
                    }

                    bool stacked = false;
                    foreach (var pair in activePerGroup)
                    {
                        histogram.TryGetValue(pair.Value, out int count);
                        histogram[pair.Value] = count + 1;

                        if (pair.Value > 1) { stacked = true; redundantRenderers += pair.Value - 1; }
                    }

                    if (stacked) charactersWithStacks++;
                }
                catch (Exception) { /* skip */ }
            }

            foreach (var pair in Sorted(histogram))
                text.AppendLine($"    {pair.Key} level(s) active  x {pair.Value} parts");

            text.AppendLine($"    => {charactersWithStacks} characters are drawing stacked LODs");
            text.AppendLine($"    => {redundantRenderers} redundant renderers that could be switched off");
        }

        /// <summary>
        /// What the renderers on a single NPC actually are - the detail the aggregate
        /// counts cannot show.
        /// </summary>
        private static void AppendCharacterBreakdown(StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("  per-renderer breakdown of the first few characters:");

            int sampled = 0;

            foreach (var character in Find.All<BaseCharacter>())
            {
                if (sampled >= 4) break;

                try
                {
                    var renderers = character.GetComponentsInChildren<Renderer>(true);
                    if (renderers == null || renderers.Length == 0) continue;

                    text.AppendLine($"    --- {character.name} ({renderers.Length} renderers) ---");

                    int skinned = 0;
                    int small = 0;
                    int live = 0;
                    int liveSkinned = 0;

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var renderer = renderers[i];
                        if (renderer == null) continue;

                        bool isSkinned = renderer.TryCast<SkinnedMeshRenderer>() != null;
                        float diagonal = renderer.bounds.size.magnitude;
                        var materials = renderer.sharedMaterials;
                        int materialCount = materials == null ? 0 : materials.Length;

                        // Enabled state matters more than the count: a renderer list is
                        // mostly dormant LOD variants.
                        bool on = renderer.enabled && renderer.gameObject.activeInHierarchy;

                        if (isSkinned) skinned++;
                        if (diagonal < 0.35f) small++;
                        if (on) { live++; if (isSkinned) liveSkinned++; }

                        // Bone count matters more than triangles on a CPU-bound frame:
                        // matrices are computed and uploaded per bone, per renderer.
                        int bones = 0;
                        if (isSkinned)
                        {
                            try
                            {
                                var rig = renderer.Cast<SkinnedMeshRenderer>().bones;
                                bones = rig == null ? 0 : rig.Length;
                            }
                            catch (Exception) { }
                        }

                        text.AppendLine(
                            $"      {(on ? "ON " : "off")}  {(isSkinned ? "skin" : "mesh")}  " +
                            $"{diagonal,6:0.00}m  {materialCount} mat  {bones,3} bones  " +
                            $"shadow={renderer.shadowCastingMode}  {renderer.name}");
                    }

                    text.AppendLine($"      => {renderers.Length} total, {skinned} skinned, " +
                                    $"{small} under 0.35m across");
                    text.AppendLine($"      => ACTIVE: {live} renderers ({liveSkinned} skinned) " +
                                    "- this is what actually costs draw calls");

                    sampled++;
                }
                catch (Exception ex)
                {
                    text.AppendLine($"    breakdown failed: {ex.Message}");
                }
            }
        }

        private static void AppendAnimators(StringBuilder text)
        {
            text.AppendLine("=== Animators ===");
            text.AppendLine("  An AlwaysAnimate animator evaluates off-screen and throws the result away.");

            try
            {
                var byMode = new Dictionary<string, int>();
                foreach (var animator in Find.All<Animator>())
                {
                    try
                    {
                        string key = animator.cullingMode.ToString();
                        byMode.TryGetValue(key, out int n);
                        byMode[key] = n + 1;
                    }
                    catch (Exception) { /* skip */ }
                }

                foreach (var pair in SortedByValue(byMode))
                    text.AppendLine($"  {pair.Key,-28} {pair.Value,6}");
            }
            catch (Exception ex) { text.AppendLine($"  failed: {ex.Message}"); }

            text.AppendLine();
        }

        private static void AppendReflectionProbes(StringBuilder text)
        {
            text.AppendLine("=== Reflection probes ===");

            try
            {
                var byMode = new Dictionary<string, int>();
                foreach (var probe in Find.All<ReflectionProbe>())
                {
                    try
                    {
                        string key = $"{probe.mode}/{probe.refreshMode}  res={probe.resolution}";
                        byMode.TryGetValue(key, out int n);
                        byMode[key] = n + 1;
                    }
                    catch (Exception) { /* skip */ }
                }

                if (byMode.Count == 0) text.AppendLine("  none in the loaded scenes");

                foreach (var pair in SortedByValue(byMode))
                    text.AppendLine($"  {pair.Value,5}  {pair.Key}");
            }
            catch (Exception ex) { text.AppendLine($"  failed: {ex.Message}"); }

            text.AppendLine();
        }

        private static List<KeyValuePair<int, int>> Sorted(Dictionary<int, int> source)
        {
            var list = new List<KeyValuePair<int, int>>(source);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }

        private static List<KeyValuePair<string, int>> SortedByValue(Dictionary<string, int> source)
        {
            var list = new List<KeyValuePair<string, int>>(source);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }
    }
}
