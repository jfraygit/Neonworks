using System;
using System.Collections.Generic;
using CrazyMinnow.SALSA;
using MagicaCloth;
using Nivalis;
using Nivalis.Traffic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering.PostProcessing;

namespace Lumen.Diagnostics
{
    // ---------------------------------------------------------------------------------
    // The ground-truth probe. Everything else is downstream of this one answer.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Halves the backbuffer. If frame time barely moves, the game is CPU bound and no
    /// amount of shader-side tuning will help.
    /// </summary>
    internal sealed class HalfResolutionProbe : Probe
    {
        internal override string Name => "Half render resolution";
        internal override string Hint =>
            "Frame time unchanged = CPU bound (chase draw calls, lights, NPCs). " +
            "Frame time halved = GPU bound (chase volumetrics, reflections, post).";

        private int _width;
        private int _height;
        private bool _fullscreen;

        protected override void Apply()
        {
            _width = Screen.width;
            _height = Screen.height;
            _fullscreen = Screen.fullScreen;

            Screen.SetResolution(Math.Max(320, _width / 2), Math.Max(240, _height / 2), _fullscreen);
            Status = $"{_width / 2}x{_height / 2}";
        }

        protected override void Restore()
        {
            Screen.SetResolution(_width, _height, _fullscreen);
            Status = null;
        }
    }

    // ---------------------------------------------------------------------------------
    // Volumetrics (HxVolumetricLighting)
    // ---------------------------------------------------------------------------------

    internal sealed class VolumetricsOffProbe : ComponentProbe<HxVolumetricCamera, bool>
    {
        internal override string Name => "Volumetrics off";
        internal override string Hint => "Total cost of the ray-marched volumetric pass.";

        protected override bool Capture(HxVolumetricCamera c) => c.enabled;
        protected override void Mutate(HxVolumetricCamera c) => c.enabled = false;
        protected override void Revert(HxVolumetricCamera c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    internal readonly struct VolumetricSamples
    {
        internal readonly int Point;
        internal readonly int Directional;
        internal VolumetricSamples(int point, int directional) { Point = point; Directional = directional; }
    }

    /// <summary>
    /// Scales the ray-march step count. Adjustable, because the useful answer is not
    /// "does it cost anything" but "how steeply does cost fall off against steps".
    /// </summary>
    internal sealed class VolumetricSampleScaleProbe : ComponentProbe<HxVolumetricCamera, VolumetricSamples>
    {
        private static readonly int[] Percent = { 75, 50, 25 };
        private int _index;

        internal override string Name => "Volumetric samples";
        internal override bool Adjustable => true;
        internal override string Hint =>
            "Steps traded for noise. Only worth shipping with TemporalSampling on - " +
            "without it a lower step count bands visibly in fog.";

        protected override VolumetricSamples Capture(HxVolumetricCamera c) =>
            new VolumetricSamples(c.SampleCount, c.DirectionalSampleCount);

        protected override void Mutate(HxVolumetricCamera c)
        {
            int pct = Percent[_index];
            c.SampleCount = Math.Max(4, c.SampleCount * pct / 100);
            c.DirectionalSampleCount = Math.Max(4, c.DirectionalSampleCount * pct / 100);
        }

        protected override void Revert(HxVolumetricCamera c, VolumetricSamples original)
        {
            c.SampleCount = original.Point;
            c.DirectionalSampleCount = original.Directional;
        }

        internal override bool Adjust(int direction)
        {
            int next = Mathf.Clamp(_index + direction, 0, Percent.Length - 1);
            if (next == _index) return false;

            bool wasActive = Active;
            if (wasActive) Toggle();   // restore original values before re-scaling
            _index = next;
            if (wasActive) Toggle();

            Status = $"{Percent[_index]}%";
            return true;
        }
    }

    // ---------------------------------------------------------------------------------
    // Planar reflections. Each of these is a full extra scene render.
    // ---------------------------------------------------------------------------------

    internal sealed class PlanarReflectionsOffProbe : ComponentProbe<PlaneReflectionScript, bool>
    {
        internal override string Name => "Planar reflections off";
        internal override string Hint => "Cost of re-rendering the scene into wet-street reflections.";

        protected override bool Capture(PlaneReflectionScript c) => c.enabled;
        protected override void Mutate(PlaneReflectionScript c) => c.enabled = false;
        protected override void Revert(PlaneReflectionScript c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    internal sealed class PlanarReflectionShadowsOffProbe : ComponentProbe<PlaneReflectionScript, bool>
    {
        internal override string Name => "Reflection shadows off";
        internal override string Hint =>
            "Shadows inside a blurred reflection. A shipping candidate if this is not free.";

        protected override bool Capture(PlaneReflectionScript c) => c.renderShadows;
        protected override void Mutate(PlaneReflectionScript c) => c.renderShadows = false;
        protected override void Revert(PlaneReflectionScript c, bool original) => c.renderShadows = original;
    }

    // ---------------------------------------------------------------------------------
    // Post-processing (PPv2)
    // ---------------------------------------------------------------------------------

    internal sealed class PostProcessOffProbe : ComponentProbe<PostProcessLayer, bool>
    {
        internal override string Name => "All post-processing off";
        internal override string Hint => "Upper bound on what the individual effect probes can add up to.";

        protected override bool Capture(PostProcessLayer c) => c.enabled;
        protected override void Mutate(PostProcessLayer c) => c.enabled = false;
        protected override void Revert(PostProcessLayer c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    /// <summary>
    /// Switches one post effect off across every volume profile in the scene.
    /// <para>
    /// Works on <c>sharedProfile</c> rather than <c>profile</c> on purpose: reading
    /// <c>profile</c> instantiates a runtime clone per volume, which is a behaviour change
    /// in its own right and would contaminate the measurement. Mutating the shared profile
    /// touches memory only, never the asset on disk, and the exact original value is put
    /// back on restore.
    /// </para>
    /// </summary>
    internal sealed class PostEffectProbe<TEffect> : Probe where TEffect : PostProcessEffectSettings
    {
        private readonly string _name;
        private readonly string _hint;
        private readonly Dictionary<int, bool> _original = new Dictionary<int, bool>();

        internal PostEffectProbe(string name, string hint) { _name = name; _hint = hint; }

        internal override string Name => _name;
        internal override string Hint => _hint;

        protected override void Apply()
        {
            _original.Clear();
            int affected = 0;

            foreach (var settings in EnumerateEffects())
            {
                try
                {
                    _original[settings.GetInstanceID()] = settings.active;
                    settings.active = false;
                    affected++;
                }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogWarning($"{Name}: skipped one effect: {ex.Message}");
                }
            }

            Status = $"{affected} hit";

            if (affected == 0)
                LumenPlugin.Log.LogWarning(
                    $"{Name}: no {typeof(TEffect).Name} found in any loaded volume. " +
                    "This probe measured nothing - do not read its delta as zero cost.");
        }

        protected override void Restore()
        {
            foreach (var settings in EnumerateEffects())
            {
                try
                {
                    if (_original.TryGetValue(settings.GetInstanceID(), out bool wasActive))
                        settings.active = wasActive;
                }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogWarning($"{Name}: could not restore one effect: {ex.Message}");
                }
            }

            _original.Clear();
            Status = null;
        }

        private static List<TEffect> EnumerateEffects()
        {
            var result = new List<TEffect>();

            foreach (var volume in Find.All<PostProcessVolume>())
            {
                try
                {
                    var profile = volume.sharedProfile;
                    if (profile == null) continue;

                    var settingsList = profile.settings;
                    if (settingsList == null) continue;

                    for (int i = 0; i < settingsList.Count; i++)
                    {
                        var effect = settingsList[i];
                        if (effect == null) continue;

                        var typed = effect.TryCast<TEffect>();
                        if (typed != null) result.Add(typed);
                    }
                }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogWarning($"Enumerating volume profile failed: {ex.Message}");
                }
            }

            return result;
        }
    }

    // ---------------------------------------------------------------------------------
    // Lights and shadows. On the Built-in pipeline these are the usual draw-call villains.
    // ---------------------------------------------------------------------------------

    internal sealed class RealtimeShadowsOffProbe : Probe
    {
        internal override string Name => "Realtime shadows off";
        internal override string Hint => "Diagnostic only - never a shipping candidate.";

        private ShadowQuality _original;

        protected override void Apply()
        {
            _original = QualitySettings.shadows;
            QualitySettings.shadows = ShadowQuality.Disable;
            Status = _original.ToString();
        }

        protected override void Restore()
        {
            QualitySettings.shadows = _original;
            Status = null;
        }
    }

    /// <summary>
    /// Strips shadow casting from every light but leaves the lights themselves lit. The
    /// gap between this and "realtime shadows off" separates shadow-map rendering from the
    /// extra forward passes the lights themselves cost.
    /// </summary>
    internal sealed class LightShadowsOffProbe : ComponentProbe<Light, LightShadows>
    {
        internal override string Name => "Per-light shadows off";
        internal override string Hint => "How much of the shadow cost is the many small point lights.";

        protected override LightShadows Capture(Light c) => c.shadows;
        protected override void Mutate(Light c) => c.shadows = LightShadows.None;
        protected override void Revert(Light c, LightShadows original) => c.shadows = original;
    }

    /// <summary>
    /// Flips the game's own Burst light culler. Note the inverted sense: applying this
    /// probe <em>enables</em> culling.
    /// </summary>
    internal sealed class LightCullingProbe : ComponentProbe<LightCullingSystem, bool>
    {
        internal override string Name => "Light culling ON";
        internal override string Hint =>
            "Applying the probe turns culling ON, so a negative delta here is a win.";

        protected override bool Capture(LightCullingSystem c) => c.isPaused;
        protected override void Mutate(LightCullingSystem c) => c.isPaused = false;
        protected override void Revert(LightCullingSystem c, bool wasPaused) => c.isPaused = wasPaused;
    }

    // ---------------------------------------------------------------------------------
    // Distance culling
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Scales every per-layer cull distance on the main camera. Tells us how much headroom
    /// distance culling has before any per-layer tuning work is done.
    /// </summary>
    internal sealed class LayerCullDistanceProbe : Probe
    {
        private static readonly int[] Percent = { 75, 50, 25 };
        private int _index;

        private Camera _camera;
        private float[] _original;

        internal override string Name => "Layer cull distances";
        internal override bool Adjustable => true;
        internal override string Hint =>
            "Blunt, all-layers version of the shipping fix. A big win here means per-layer " +
            "tuning on small props is worth doing properly.";

        protected override void Apply()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                Status = "no main camera";
                LumenPlugin.Log.LogWarning($"{Name}: Camera.main is null. This probe measured nothing.");
                return;
            }

            _original = _camera.layerCullDistances;

            var scaled = new float[32];
            float far = _camera.farClipPlane;
            int pct = Percent[_index];

            for (int i = 0; i < 32; i++)
            {
                // A zero entry means "use the far plane", so scale the far plane in its place.
                float current = (_original != null && i < _original.Length && _original[i] > 0f)
                    ? _original[i]
                    : far;
                scaled[i] = current * pct / 100f;
            }

            _camera.layerCullDistances = scaled;
            Status = $"{pct}%";
        }

        protected override void Restore()
        {
            if (_camera != null && _original != null)
                _camera.layerCullDistances = _original;

            _camera = null;
            _original = null;
            Status = null;
        }

        internal override bool Adjust(int direction)
        {
            int next = Mathf.Clamp(_index + direction, 0, Percent.Length - 1);
            if (next == _index) return false;

            bool wasActive = Active;
            if (wasActive) Toggle();
            _index = next;
            if (wasActive) Toggle();

            Status = $"{Percent[_index]}%";
            return true;
        }
    }

    // ---------------------------------------------------------------------------------
    // NPCs. Draw cost and simulation cost are different problems with different fixes,
    // so they get separate probes and must not be conflated.
    // ---------------------------------------------------------------------------------

    /// <summary>Hides NPC geometry but leaves every behaviour running. Isolates draw cost.</summary>
    internal sealed class NpcRenderersHiddenProbe : Probe
    {
        internal override string Name => "NPC renderers hidden";
        internal override string Hint => "NPC draw cost only - their AI, IK and lip-sync all keep running.";

        private readonly Dictionary<int, bool> _original = new Dictionary<int, bool>();

        protected override void Apply()
        {
            _original.Clear();
            int affected = 0;

            foreach (var character in Find.All<BaseCharacter>())
            {
                try
                {
                    var renderers = character.GetComponentsInChildren<Renderer>(true);
                    if (renderers == null) continue;

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var renderer = renderers[i];
                        if (renderer == null) continue;

                        _original[renderer.GetInstanceID()] = renderer.enabled;
                        renderer.enabled = false;
                        affected++;
                    }
                }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogWarning($"{Name}: skipped one character: {ex.Message}");
                }
            }

            Status = $"{affected} renderers";

            if (affected == 0)
                LumenPlugin.Log.LogWarning(
                    $"{Name}: found no NPC renderers. This probe measured nothing.");
        }

        protected override void Restore()
        {
            // Walk the characters again rather than every Renderer in the scene: the latter
            // is a visible hitch that would pollute the next reading.
            foreach (var character in Find.All<BaseCharacter>())
            {
                try
                {
                    var renderers = character.GetComponentsInChildren<Renderer>(true);
                    if (renderers == null) continue;

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var renderer = renderers[i];
                        if (renderer == null) continue;

                        if (_original.TryGetValue(renderer.GetInstanceID(), out bool wasEnabled))
                            renderer.enabled = wasEnabled;
                    }
                }
                catch (Exception ex)
                {
                    LumenPlugin.Log.LogWarning($"{Name}: could not restore one character: {ex.Message}");
                }
            }

            _original.Clear();
            Status = null;
        }
    }

    /// <summary>
    /// Stops animators evaluating when their renderers are off-screen. If the game already
    /// culls, this reads as zero and costs nothing to have asked.
    /// </summary>
    internal sealed class AnimatorCullingProbe : ComponentProbe<Animator, AnimatorCullingMode>
    {
        internal override string Name => "Animator culling (off-screen)";
        internal override string Hint =>
            "Shippable and invisible if it wins: an off-screen animator's output is discarded anyway.";

        protected override AnimatorCullingMode Capture(Animator c) => c.cullingMode;
        protected override void Mutate(Animator c) => c.cullingMode = AnimatorCullingMode.CullCompletely;
        protected override void Revert(Animator c, AnimatorCullingMode original) => c.cullingMode = original;
    }

    /// <summary>
    /// A skinned renderer with <c>updateWhenOffscreen</c> set recomputes its bounds from
    /// the skeleton every frame whether or not anyone can see it.
    /// </summary>
    internal sealed class OffscreenBoundsProbe : ComponentProbe<SkinnedMeshRenderer, bool>
    {
        internal override string Name => "Off-screen bounds update off";
        internal override string Hint => "Per-skinned-mesh bounds recalculation. Shippable if it wins.";

        protected override bool Capture(SkinnedMeshRenderer c) => c.updateWhenOffscreen;
        protected override void Mutate(SkinnedMeshRenderer c) => c.updateWhenOffscreen = false;
        protected override void Revert(SkinnedMeshRenderer c, bool original) => c.updateWhenOffscreen = original;
    }

    /// <summary>
    /// Bones per vertex. Four to two halves the skinning maths.
    /// </summary>
    internal sealed class SkinWeightsProbe : Probe
    {
        private static readonly SkinWeights[] Levels = { SkinWeights.TwoBones, SkinWeights.OneBone };
        private int _index;

        private SkinWeights _original;

        internal override string Name => "Skin weights";
        internal override bool Adjustable => true;
        internal override string Hint =>
            "CPU skinning cost. Two bones is a shipping candidate; one bone is diagnostic only.";

        protected override void Apply()
        {
            _original = QualitySettings.skinWeights;
            QualitySettings.skinWeights = Levels[_index];
            Status = $"{Levels[_index]} (was {_original})";
        }

        protected override void Restore()
        {
            QualitySettings.skinWeights = _original;
            Status = Levels[_index].ToString();
        }

        internal override bool Adjust(int direction)
        {
            int next = Mathf.Clamp(_index + direction, 0, Levels.Length - 1);
            if (next == _index) return false;

            bool wasActive = Active;
            if (wasActive) Toggle();
            _index = next;
            if (wasActive) Toggle();

            Status = Levels[_index].ToString();
            return true;
        }
    }

    /// <summary>
    /// Scales Unity's global LOD bias. Higher values hold objects at their most detailed
    /// mesh further from the camera.
    /// </summary>
    internal sealed class LodBiasProbe : Probe
    {
        private static readonly float[] Levels = { 5f, 3f, 2f };
        private int _index;

        private float _original;

        internal override string Name => "LOD bias";
        internal override bool Adjustable => true;
        internal override string Hint =>
            "Measure, then judge by eye: this one can show.";

        protected override void Apply()
        {
            _original = QualitySettings.lodBias;
            QualitySettings.lodBias = Levels[_index];
            Status = $"{Levels[_index]:0.#} (was {_original:0.#})";
        }

        protected override void Restore()
        {
            QualitySettings.lodBias = _original;
            Status = $"{Levels[_index]:0.#}";
        }

        internal override bool Adjust(int direction)
        {
            int next = Mathf.Clamp(_index + direction, 0, Levels.Length - 1);
            if (next == _index) return false;

            bool wasActive = Active;
            if (wasActive) Toggle();
            _index = next;
            if (wasActive) Toggle();

            Status = $"{Levels[_index]:0.#}";
            return true;
        }
    }

    // ---------------------------------------------------------------------------------
    // Navigation. No per-character component has an Update(), so the cost that disabling
    // Character removed has to be native nav simulation plus CharacterManager's own loop.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Crowd avoidance quality. RVO is the expensive part of a NavMeshAgent and scales
    /// badly with agent count.
    /// </summary>
    internal sealed class AvoidanceQualityProbe : ComponentProbe<NavMeshAgent, ObstacleAvoidanceType>
    {
        private static readonly ObstacleAvoidanceType[] Levels =
        {
            ObstacleAvoidanceType.LowQualityObstacleAvoidance,
            ObstacleAvoidanceType.NoObstacleAvoidance
        };

        private int _index;

        internal override string Name => "Agent avoidance quality";
        internal override bool Adjustable => true;
        internal override string Hint =>
            "Low quality is a shipping candidate for distant NPCs. None is diagnostic - " +
            "crowds will clip through each other.";

        protected override ObstacleAvoidanceType Capture(NavMeshAgent c) => c.obstacleAvoidanceType;
        protected override void Mutate(NavMeshAgent c) => c.obstacleAvoidanceType = Levels[_index];
        protected override void Revert(NavMeshAgent c, ObstacleAvoidanceType original) =>
            c.obstacleAvoidanceType = original;

        internal override bool Adjust(int direction)
        {
            int next = Mathf.Clamp(_index + direction, 0, Levels.Length - 1);
            if (next == _index) return false;

            bool wasActive = Active;
            if (wasActive) Toggle();
            _index = next;
            if (wasActive) Toggle();

            Status = Levels[_index].ToString();
            return true;
        }
    }

    /// <summary>Upper bound on what navigation costs at all. Diagnostic only - NPCs stop walking.</summary>
    internal sealed class NavAgentsOffProbe : ComponentProbe<NavMeshAgent, bool>
    {
        internal override string Name => "Nav agents off";
        internal override string Hint => "Ceiling for all navigation cost. Diagnostic - NPCs freeze.";

        protected override bool Capture(NavMeshAgent c) => c.enabled;
        protected override void Mutate(NavMeshAgent c) => c.enabled = false;
        protected override void Revert(NavMeshAgent c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    /// <summary>
    /// The only component in the character stack with an Update(), and so the only place
    /// a per-frame managed cost could live.
    /// </summary>
    internal sealed class CharacterManagerOffProbe : ComponentProbe<CharacterManager, bool>
    {
        internal override string Name => "CharacterManager Update off";
        internal override string Hint => "Isolates the one managed per-frame loop over all characters.";

        protected override bool Capture(CharacterManager c) => c.enabled;
        protected override void Mutate(CharacterManager c) => c.enabled = false;
        protected override void Revert(CharacterManager c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    // ---------------------------------------------------------------------------------
    // Remaining untested candidates, all CPU-side draw-call work.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A realtime reflection probe re-renders the scene into six cubemap faces. On a
    /// CPU-bound frame that is draw calls, not shading.
    /// </summary>
    internal sealed class RealtimeReflectionProbesProbe : Probe
    {
        internal override string Name => "Realtime reflection probes off";
        internal override string Hint => "Each realtime probe refresh is six more scene renders.";

        private bool _original;

        protected override void Apply()
        {
            _original = QualitySettings.realtimeReflectionProbes;
            QualitySettings.realtimeReflectionProbes = false;
            Status = _original ? "was on" : "was already off";
        }

        protected override void Restore()
        {
            QualitySettings.realtimeReflectionProbes = _original;
            Status = null;
        }
    }

    /// <summary>
    /// Forward rendering costs one extra pass per renderer per pixel light, so this cap
    /// bounds a real cost in a scene with many lights.
    /// </summary>
    internal sealed class PixelLightCountProbe : Probe
    {
        private static readonly int[] Counts = { 4, 2, 1 };
        private int _index;

        private int _original;

        internal override string Name => "Pixel light count";
        internal override bool Adjustable => true;
        internal override string Hint =>
            "Extra forward passes per renderer. Lights beyond the cap fall back to vertex/SH, " +
            "which IS visible on some surfaces - measure first, judge by eye second.";

        protected override void Apply()
        {
            _original = QualitySettings.pixelLightCount;
            QualitySettings.pixelLightCount = Counts[_index];
            Status = $"{Counts[_index]} (was {_original})";
        }

        protected override void Restore()
        {
            QualitySettings.pixelLightCount = _original;
            Status = Counts[_index].ToString();
        }

        internal override bool Adjust(int direction)
        {
            int next = Mathf.Clamp(_index + direction, 0, Counts.Length - 1);
            if (next == _index) return false;

            bool wasActive = Active;
            if (wasActive) Toggle();
            _index = next;
            if (wasActive) Toggle();

            Status = Counts[_index].ToString();
            return true;
        }
    }

    /// <summary>
    /// The shadow pass re-draws the scene once per cascade, so cascade count and shadow
    /// resolution are draw-call costs.
    /// </summary>
    internal sealed class ShadowCostProbe : Probe
    {
        internal override string Name => "Shadows: 1 cascade, High res";
        internal override string Hint =>
            "Halves the shadow passes without shortening shadow distance. Judge cascade " +
            "banding by eye before shipping.";

        private int _originalCascades;
        private ShadowResolution _originalResolution;

        protected override void Apply()
        {
            _originalCascades = QualitySettings.shadowCascades;
            _originalResolution = QualitySettings.shadowResolution;

            QualitySettings.shadowCascades = 1;
            QualitySettings.shadowResolution = ShadowResolution.High;

            Status = $"was {_originalCascades} / {_originalResolution}";
        }

        protected override void Restore()
        {
            QualitySettings.shadowCascades = _originalCascades;
            QualitySettings.shadowResolution = _originalResolution;
            Status = null;
        }
    }

    /// <summary>
    /// Stops NPC behaviour Updates but leaves them drawn. Isolates simulation cost.
    /// Diagnostic only: AI state will be stale until the area reloads.
    /// </summary>
    internal sealed class NpcBehavioursOffProbe : ComponentProbe<BaseCharacter, bool>
    {
        internal override string Name => "NPC behaviours off";
        internal override string Hint =>
            "NPC simulation cost only. Diagnostic - AI stays stale until the area reloads.";

        protected override bool Capture(BaseCharacter c) => c.enabled;
        protected override void Mutate(BaseCharacter c) => c.enabled = false;
        protected override void Revert(BaseCharacter c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    internal sealed class SalsaOffProbe : ComponentProbe<Salsa, bool>
    {
        internal override string Name => "Lip-sync off (SALSA)";
        internal override string Hint => "Per-character lip-sync. A distance LOD candidate if it shows up.";

        protected override bool Capture(Salsa c) => c.enabled;
        protected override void Mutate(Salsa c) => c.enabled = false;
        protected override void Revert(Salsa c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    internal sealed class EmoterOffProbe : ComponentProbe<Emoter, bool>
    {
        internal override string Name => "Facial emotes off";
        internal override string Hint => "SALSA's Emoter, the other half of the per-character face work.";

        protected override bool Capture(Emoter c) => c.enabled;
        protected override void Mutate(Emoter c) => c.enabled = false;
        protected override void Revert(Emoter c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    internal sealed class CharacterIkOffProbe : ComponentProbe<CharacterIK, bool>
    {
        internal override string Name => "Character IK off";
        internal override string Hint => "Foot, hand and head IK. Runs per character, every frame.";

        protected override bool Capture(CharacterIK c) => c.enabled;
        protected override void Mutate(CharacterIK c) => c.enabled = false;
        protected override void Revert(CharacterIK c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    internal sealed class MagicaClothOffProbe : ComponentProbe<MagicaPhysicsManager, bool>
    {
        internal override string Name => "Cloth physics off";
        internal override string Hint => "MagicaCloth's whole simulation step.";

        protected override bool Capture(MagicaPhysicsManager c) => c.enabled;
        protected override void Mutate(MagicaPhysicsManager c) => c.enabled = false;
        protected override void Revert(MagicaPhysicsManager c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    internal sealed class TrafficOffProbe : ComponentProbe<TrafficVehicle, bool>
    {
        internal override string Name => "Traffic off";
        internal override string Hint => "Vehicle Updates. Their geometry stays drawn.";

        protected override bool Capture(TrafficVehicle c) => c.enabled;
        protected override void Mutate(TrafficVehicle c) => c.enabled = false;
        protected override void Revert(TrafficVehicle c, bool wasEnabled) => c.enabled = wasEnabled;
    }

    // ---------------------------------------------------------------------------------
    // Leads from settings.ini that were never followed up.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Switches off every game Canvas, which stops UI rendering and layout rebuilds, to
    /// establish the ceiling for anything UI-shaped.
    /// <para>Diagnostic only - the HUD disappears while it is on.</para>
    /// </summary>
    internal sealed class GameUiOffProbe : ComponentProbe<Canvas, bool>
    {
        internal override string Name => "Game UI off";
        internal override string Hint =>
            "Ceiling for anything UI-shaped, including the shipped UIOptimization setting. " +
            "Near zero here means that setting is not worth chasing.";

        protected override bool Capture(Canvas c) => c.enabled;
        protected override void Mutate(Canvas c) => c.enabled = false;
        protected override void Revert(Canvas c, bool wasEnabled) => c.enabled = wasEnabled;
    }
}
