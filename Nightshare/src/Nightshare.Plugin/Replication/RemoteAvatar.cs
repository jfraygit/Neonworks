using System;
using System.Collections.Generic;
using System.Text;
using Nightshare.Core;
using UnityEngine;

namespace Nightshare.Replication
{
    /// <summary>
    /// A visible body for a remote player, built by cloning a live NPC and removing
    /// everything that would make it act on its own.
    /// <para>
    /// <b>Why an NPC and not the player's own character.</b> The rig probe showed
    /// <c>PlayerCharacter</c> is a first person hands rig: arms, IK targets and held
    /// props, with no torso, legs or head anywhere in its 44 transforms. Cloning it would
    /// produce disembodied hands gliding through the city. <c>Nivalis.Character</c> is the
    /// NPC class and has an actual body.
    /// </para>
    /// <para>
    /// <b>Why components are destroyed rather than disabled.</b> Disabling a behaviour is
    /// not reliably enough to stop it: some AI keeps running regardless of
    /// <c>enabled</c>, and a clone that still thinks it is an NPC will wander off,
    /// take jobs, join queues and talk to people. The clone is reduced to a puppet:
    /// transform, renderers and animator, nothing else.
    /// </para>
    /// </summary>
    internal sealed class RemoteAvatar
    {
        /// <summary>
        /// Components kept on a clone. Everything else on every object in the hierarchy is
        /// destroyed. An allow list rather than a deny list, because a deny list silently
        /// lets through anything the game adds in a future patch.
        /// </summary>
        private static readonly HashSet<string> KeepComponents = new(StringComparer.Ordinal)
        {
            "Transform",
            "Animator",
            "SkinnedMeshRenderer", "MeshRenderer", "MeshFilter",
        };

        // Deliberately NOT kept:
        //
        //   RectTransform  The source NPC brings nameplate and AR UI canvases. Their
        //                  components strip away but the objects remain as empty junk.
        //                  Those whole GameObjects are removed instead.
        //
        //   LODGroup       It can disable renderers on our behalf, and a puppet that is
        //                  always near the player has nothing to gain from LODs. Keeping
        //                  it adds a second thing that can hide the body.

        public PeerId Peer { get; }
        public string Name { get; private set; }
        public GameObject GameObject { get; private set; }

        private Transform _transform;
        private Vector3 _targetPosition;
        private float _targetYaw;
        private bool _hasTarget;
        private float _visibilityTimer;
        private GameObject _beacon;

        private Animator _animator;
        private float _reportedSpeed;
        private float _smoothedSpeed;

        /// <summary>
        /// Faster than this is a teleport, not movement. A sprinting human is single
        /// digits; anything past this came from a discontinuity.
        /// </summary>
        private const float MaxBelievableSpeed = 15f;

        /// <summary>
        /// Floor for the animator's Speed parameter while a moving gait is playing.
        /// <para>
        /// Below roughly this value the walk pose collapses toward the ground. Measured
        /// hip height tracks Speed one to one under 0.9 and saturates above it, so 1.0
        /// sits just past the collapse with no visible effect on the animation.
        /// </para>
        /// </summary>
        private const float MinimumPoseSpeed = 1.0f;

        /// <summary>
        /// The parameters this rig actually uses, read from a live animator rather than
        /// guessed. Locomotion is a set of blend WEIGHTS, not booleans: <c>Walk</c> and
        /// <c>Idle</c> are floats that cross-fade, and <c>Speed</c> only scales playback
        /// within whichever is weighted. Driving Speed alone leaves the body frozen
        /// part-way into a step, which is exactly what the first attempt produced.
        /// </summary>
        private const string SpeedParam = "Speed";
        private const string WalkParam = "Walk";
        private const string IdleParam = "Idle";

        /// <summary>
        /// Running. Blended in above <see cref="SprintSpeed"/>.
        /// <para>
        /// This was in the "hold at zero" list to begin with, which meant sprinting could
        /// never animate: a running player just walked faster. Reported from play.
        /// </para>
        /// </summary>
        private const string SprintParam = "WalkSprint";

        /// <summary>
        /// Where walking ends and running begins, in metres per second.
        /// <para>
        /// Configurable because the first values here were guessed and were wrong in both
        /// directions: at 1.5 a normal walk already read as a full run. Tune them against
        /// the speeds the log reports rather than by feel.
        /// </para>
        /// </summary>
        private static float WalkSpeed => NightsharePlugin.Settings.WalkSpeed.Value;
        private static float SprintSpeed => NightsharePlugin.Settings.SprintSpeed.Value;

        /// <summary>Peak speed seen, so the thresholds can be set from real numbers.</summary>
        private float _peakSpeed;
        private float _speedReportTimer;

        /// <summary>
        /// The three gaits, chosen one at a time.
        /// <para>
        /// Not weights. Blending idle into walk put the body's feet through the ground,
        /// while running, the one case where a single weight was 1.0 and the rest 0,
        /// always looked right.
        /// </para>
        /// </summary>
        private enum Gait { Idle, Walk, Run }

        private Gait _currentGait = Gait.Idle;
        private Gait _lastSampledGait = Gait.Idle;

        /// <summary>
        /// How far past a threshold the speed must go before the gait changes. Without
        /// this, a player moving near a boundary flickers between two gaits several times
        /// a second, which looks worse than either gait being slightly wrong.
        /// </summary>
        private const float GaitHysteresis = 1.0f;

        /// <summary>Below this, treat the player as standing still.</summary>
        private const float StandingSpeed = 0.15f;

        private Gait ChooseGait(float speed)
        {
            // Widen the band the current gait occupies, so leaving it takes more than
            // brushing the threshold.
            var idleLimit = StandingSpeed + (_currentGait == Gait.Idle ? GaitHysteresis : 0f);
            var runLimit = SprintSpeed - (_currentGait == Gait.Run ? GaitHysteresis : 0f);

            if (speed <= idleLimit) return Gait.Idle;
            if (speed >= runLimit) return Gait.Run;
            return Gait.Walk;
        }

        // --- stop dip diagnostic ---
        //
        // The body visibly sinks through the ground for a moment when a remote player
        // stops. Two candidate causes need opposite fixes, and only numbers separate them:
        //
        //   The received position really dips. Then we are replicating faithfully and the
        //   fault is upstream, in what the sender reads off its own transform.
        //
        //   The position holds steady and the body sinks anyway. Then it is the animator
        //   or the rig origin, and the transform is innocent.
        //
        // So: on the transition to standing still, record both for a second.
        private float _dipSampleTimer;
        private int _dipSamplesLeft;

        /// <summary>
        /// The gaits with no equivalent on a remote player. Held at zero so a stale weight
        /// inherited from the source NPC cannot blend a drunk stagger or a panicked run
        /// into the movement.
        /// </summary>
        private static readonly string[] OtherGaits =
        {
            "WalkHurry", "WalkHappy", "WalkScared", "WalkSad",
            "WalkStroll", "WalkWindy", "WalkPatrol", "WalkDrunk", "WalkStaff", "WalkCold",
        };

        private int _speedHash = -1;
        private int _walkHash = -1;
        private int _idleHash = -1;
        private int _sprintHash = -1;
        private readonly List<int> _otherGaitHashes = new();

        public bool IsAlive => GameObject != null;

        private RemoteAvatar(PeerId peer, string name, GameObject go)
        {
            Peer = peer;
            Name = name;
            GameObject = go;
            _transform = go.transform;
        }

        /// <summary>
        /// Clone a source NPC into a puppet. Returns null if no source is available, which
        /// is normal before the world has populated.
        /// </summary>
        public static RemoteAvatar TryCreate(PeerId peer, string name, Vector3 position, float yaw)
        {
            var source = FindSourceCharacter();
            if (source == null) return null;

            // CLONE FROM AN INACTIVE SOURCE, ALWAYS.
            //
            // Instantiating a live, active NPC runs Awake and OnEnable on the clone
            // immediately, before a single component can be stripped. In that instant the
            // clone registers itself with the object pool, the AI agent registry and
            // anything else its components talk to. Stripping afterwards is far too late:
            // the game already believes it owns a second NPC, and says so later as
            //
            //   Pooled element Nightshare_RemotePlayer_... should not be destroyed
            //   Next exception occured on agent <guid>
            //   NullReferenceException
            //
            // Cloning from a deactivated source produces an inactive clone whose Awake
            // never runs, so nothing registers anywhere. The behaviours are gone before it
            // is allowed to draw its first breath.
            GameObject clone;
            var sourceWasActive = source.activeSelf;

            try
            {
                source.SetActive(false);
                try
                {
                    clone = UnityEngine.Object.Instantiate(source, position, Quaternion.Euler(0f, yaw, 0f));
                }
                finally
                {
                    // Put the real NPC back however it was found, whatever happens next.
                    source.SetActive(sourceWasActive);
                }
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogError($"Avatar: could not clone a character: {ex.Message}");
                return null;
            }

            if (clone == null) return null;

            clone.name = $"Nightshare_RemotePlayer_{peer.ToShortString()}";

            var stripped = Strip(clone);
            NightsharePlugin.Logger?.LogInfo(
                $"Avatar: spawned '{name}' from a clone of '{source.name}', " +
                $"stripped {stripped} component(s)");

            // Detach from whatever pool or parent the source belonged to, so nothing
            // recycles our puppet out from under us.
            try { clone.transform.SetParent(null, worldPositionStays: true); } catch { }

            // Only now, with every behaviour already removed, is it safe to wake up. The
            // Awake calls that run here belong to the handful of components on the keep
            // list, none of which register with anything.
            try { clone.SetActive(true); } catch { }

            var avatar = new RemoteAvatar(peer, name, clone);
            avatar.ForceVisible(logResult: true);
            avatar.BindAnimator();

            if (NightsharePlugin.Settings.ShowAvatarBeacon.Value) avatar.AddBeacon();

            return avatar;
        }

        /// <summary>
        /// Destroy every component in the hierarchy that is not on the allow list.
        /// <para>
        /// Uses Destroy rather than DestroyImmediate: DestroyImmediate refuses to remove a
        /// component another one declares as required, which would leave exactly the AI
        /// behaviours we most want gone. Destroy is deferred to end of frame, so a clone
        /// may think for one frame; harmless, since it is repositioned every frame anyway.
        /// </para>
        /// </summary>
        private static int Strip(GameObject root)
        {
            var removed = 0;
            var detachedPooled = 0;
            var kept = new StringBuilder();

            // Remove the NPC's UI canvases outright. Stripping their components leaves
            // empty RectTransform objects behind, which is how the first version ended up
            // dragging two dozen of them around.
            try
            {
                // DestroyImmediate, so these objects are GONE before the component sweep
                // below runs. With deferred Destroy they survive until end of frame, the
                // sweep then tries to remove their RectTransforms individually, and Unity
                // refuses every one of them:
                //
                //   Can't remove RectTransform because Canvas depends on it
                //   Can't destroy RectTransform component of 'Role_Cook'...
                //
                // which is dozens of red errors per avatar spawn.
                var rects = root.GetComponentsInChildren<RectTransform>(includeInactive: true);
                foreach (var rect in rects)
                {
                    if (rect == null || rect.gameObject == root) continue;
                    try { UnityEngine.Object.DestroyImmediate(rect.gameObject); removed++; }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }

            // Detach the game's pooled children BEFORE anything else touches them.
            //
            // The source NPC brings its own pooled objects along, a P_Vendor among them,
            // and the clone inherits the lot. The game notices and complains:
            //
            //   Pooled element P_Vendor (Clone) should not be destroyed, but released
            //   back to the pool instead!  Parent:Nightshare_RemotePlayer_host
            //
            // That is not cosmetic. Those children keep their own behaviour and the pool
            // still believes it owns them, so it can reclaim pieces of a body we are busy
            // driving. Unparent them rather than destroying them, so the pool keeps the
            // objects it is tracking and simply gets them back where it expects.
            try
            {
                var pooled = root.GetComponentsInChildren<Nivalis.PooledElement>(includeInactive: true);
                foreach (var element in pooled)
                {
                    if (element == null) continue;

                    var go = element.gameObject;
                    if (go == null || go == root) continue;   // the root is handled below

                    try
                    {
                        go.transform.SetParent(null, worldPositionStays: true);
                        go.SetActive(false);
                        detachedPooled++;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }

            try
            {
                var components = root.GetComponentsInChildren<Component>(includeInactive: true);
                foreach (var c in components)
                {
                    if (c == null) continue;

                    string typeName;
                    try { typeName = c.GetIl2CppType().Name; }
                    catch (Exception) { continue; }

                    // A RectTransform cannot be removed while its Canvas exists, and the
                    // whole object should have gone above anyway. Skip rather than
                    // generate an error Unity will refuse.
                    if (typeName == "RectTransform") continue;

                    if (KeepComponents.Contains(typeName))
                    {
                        if (kept.Length < 400 && typeName != "Transform")
                            kept.Append(typeName).Append(' ');
                        continue;
                    }

                    try
                    {
                        // DestroyImmediate, not Destroy, because the clone is still
                        // inactive and must be fully disarmed before it is switched on.
                        // Deferred destruction would leave every behaviour alive for the
                        // frame in which the body first wakes, which is exactly the window
                        // where it registers with the pool and the agent system.
                        UnityEngine.Object.DestroyImmediate(c, allowDestroyingAssets: false);
                        removed++;
                    }
                    catch (Exception)
                    {
                        // DestroyImmediate refuses to remove a component another one
                        // declares as required. Fall back to the deferred form rather than
                        // leaving an AI behaviour in place.
                        try { UnityEngine.Object.Destroy(c); removed++; }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Avatar: strip failed: {ex.Message}");
            }

            if (kept.Length > 0)
                NightsharePlugin.Logger?.LogInfo($"Avatar: kept {kept.ToString().TrimEnd()}");

            if (detachedPooled > 0)
                NightsharePlugin.Logger?.LogInfo(
                    $"Avatar: detached {detachedPooled} pooled child object(s) back to the game");

            return removed;
        }

        /// <summary>
        /// Find any live NPC to clone. Prefers one that is active and has a renderer, so
        /// the puppet is actually visible.
        /// </summary>
        private static GameObject FindSourceCharacter()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Nivalis.Character>();
                if (all == null || all.Length == 0) return null;

                foreach (var c in all)
                {
                    if (c == null) continue;
                    var go = c.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;

                    // Skip anything we made, or a clone of a clone compounds every fault.
                    if (go.name.StartsWith("Nightshare_", StringComparison.Ordinal)) continue;

                    var renderer = go.GetComponentInChildren<SkinnedMeshRenderer>();
                    if (renderer != null) return go;
                }
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Avatar: could not find a source character: {ex.Message}");
            }

            return null;
        }

        // ---------------------------------------------------------------- driving

        /// <summary>
        /// Switch every renderer back on and make sure the whole hierarchy is active.
        /// <para>
        /// <b>Needed because something else turns them off.</b> The avatar is a cloned NPC,
        /// so anything that culls NPCs for performance culls it too. Confirmed in a real
        /// session: the body spawned in exactly the right place and was invisible, while
        /// the Lumen performance mod reported "136 of 136 culled" in the same log. The
        /// game's own <c>BaseCharacter</c> culling is a second candidate, and a source NPC
        /// that was already culled hands the clone a disabled renderer to begin with.
        /// </para>
        /// <para>
        /// Re-asserted periodically rather than once, since a culling pass can run at any
        /// time and would otherwise win.
        /// </para>
        /// </summary>
        public void ForceVisible(bool logResult = false)
        {
            if (!IsAlive) return;

            try
            {
                if (!GameObject.activeSelf) GameObject.SetActive(true);

                var renderers = GameObject.GetComponentsInChildren<Renderer>(includeInactive: true);
                var reEnabled = 0;
                var names = logResult ? new StringBuilder() : null;

                foreach (var r in renderers)
                {
                    if (r == null) continue;

                    var go = r.gameObject;

                    // Leave the reduced LOD meshes alone. Measured on a real clone: the
                    // disabled renderers were Body_LOD_0 through Body_LOD_004 and the same
                    // for the head. Switching them all on stacks five copies of the body
                    // in the same place, which costs five times the mesh work and renders
                    // identically because LOD0 sits on top.
                    if (go != null && IsReducedLod(go.name)) continue;

                    var wasOff = !r.enabled;
                    var objectWasOff = go != null && !go.activeSelf;

                    if (wasOff) { r.enabled = true; reEnabled++; }
                    if (objectWasOff) { go.SetActive(true); reEnabled++; }

                    // Record WHAT was off, not just how many. The names distinguish LOD
                    // variants from unworn AvatarParts, and those need opposite treatment:
                    // a hidden LOD should stay hidden, an unworn hairstyle must stay off,
                    // but a distance-culled body has to come back on.
                    if (names != null && (wasOff || objectWasOff) && names.Length < 600)
                        names.Append(go != null ? go.name : r.name).Append(' ');
                }

                if (logResult)
                {
                    NightsharePlugin.Logger?.LogInfo(
                        $"Avatar: {renderers.Length} renderer(s), re-enabled {reEnabled} that were off");

                    if (names.Length > 0)
                        NightsharePlugin.Logger?.LogInfo($"Avatar: were off -> {names.ToString().TrimEnd()}");
                }
            }
            catch (Exception ex)
            {
                if (logResult)
                    NightsharePlugin.Logger?.LogWarning($"Avatar: could not force visibility: {ex.Message}");
            }
        }

        /// <summary>
        /// Put a bright floating marker over the avatar's head.
        /// <para>
        /// The city is full of NPCs and the avatar is a clone of one, so without this it
        /// is genuinely impossible to tell which body is the other player. That is not a
        /// cosmetic problem during development: it makes a working feature and a broken
        /// one look identical.
        /// </para>
        /// <para>Diagnostic, not a shipping feature. Nameplates replace it later.</para>
        /// </summary>
        public void AddBeacon()
        {
            if (!IsAlive) return;

            try
            {
                var beacon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                beacon.name = "Nightshare_Beacon";

                // A collider on a floating marker would block doorways and shove the
                // player around.
                var collider = beacon.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.Destroy(collider);

                beacon.transform.SetParent(_transform, worldPositionStays: false);
                beacon.transform.localPosition = new Vector3(0f, 2.3f, 0f);
                beacon.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);

                var renderer = beacon.GetComponent<Renderer>();
                if (renderer != null && renderer.material != null)
                {
                    // Cyan reads clearly against this game's palette, day or night.
                    renderer.material.color = new Color(0f, 1f, 1f, 1f);
                    try { renderer.material.SetColor("_EmissionColor", new Color(0f, 1f, 1f, 1f)); }
                    catch (Exception) { }
                }

                _beacon = beacon;
                NightsharePlugin.Logger?.LogInfo("Avatar: beacon attached, look for the cyan sphere");
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Avatar: could not attach a beacon: {ex.Message}");
            }
        }

        /// <summary>
        /// Find the animator and work out which of its parameters drive walking.
        /// <para>
        /// The parameters are read from the controller at runtime rather than assumed.
        /// The game's own <c>Character</c> drove this and we destroyed it, so the avatar
        /// keeps a valid animator with nobody telling it to move: the body slides along in
        /// its idle pose. Every parameter found is logged, so if the name matching below
        /// misses, the log says exactly what the real names are.
        /// </para>
        /// </summary>
        public void BindAnimator()
        {
            if (!IsAlive) return;

            try
            {
                _animator = GameObject.GetComponentInChildren<Animator>();
                if (_animator == null)
                {
                    NightsharePlugin.Logger?.LogWarning("Avatar: no Animator, the body will slide.");
                    return;
                }

                // Root motion would fight the position we are replicating.
                _animator.applyRootMotion = false;

                // The game carries AnimatorDeltaTime and AnimatorUpdateStep on Character,
                // which is the signature of stepping animators manually on a stagger. If
                // the source was being driven that way, the clone inherits an animator
                // nobody advances, and no parameter value would ever show. Hand it back to
                // Unity explicitly.
                if (!_animator.enabled)
                {
                    _animator.enabled = true;
                    NightsharePlugin.Logger?.LogInfo(
                        "Avatar: the animator was disabled (the game steps NPC animators " +
                        "itself), re-enabled so Unity drives it");
                }

                // Never stop animating just because the body is off camera. A remote
                // player freezing whenever you look away would read as a desync.
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // Match exact names. The rig has 103 parameters, many of them containing
                // "walk", so substring matching picks up gaits we do not want.
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in _animator.parameters)
                    if (p != null) present.Add(p.name);

                _speedHash = present.Contains(SpeedParam) ? Animator.StringToHash(SpeedParam) : -1;
                _walkHash = present.Contains(WalkParam) ? Animator.StringToHash(WalkParam) : -1;
                _idleHash = present.Contains(IdleParam) ? Animator.StringToHash(IdleParam) : -1;
                _sprintHash = present.Contains(SprintParam) ? Animator.StringToHash(SprintParam) : -1;

                foreach (var gait in OtherGaits)
                    if (present.Contains(gait)) _otherGaitHashes.Add(Animator.StringToHash(gait));

                NightsharePlugin.Logger?.LogInfo(
                    $"Avatar: animator bound, {present.Count} parameter(s). " +
                    $"Speed={_speedHash != -1} Walk={_walkHash != -1} Idle={_idleHash != -1}, " +
                    $"{_otherGaitHashes.Count} other gait(s) held at zero");

                if (_walkHash == -1)
                {
                    NightsharePlugin.Logger?.LogWarning(
                        $"Avatar: this rig has no '{WalkParam}' parameter, so the body will slide. " +
                        $"Parameters present: {string.Join(" ", present)}");
                }
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Avatar: could not bind the animator: {ex.Message}");
                _animator = null;
            }
        }

        /// <summary>
        /// Feed the animator how fast the remote player is moving.
        /// <para>
        /// The speed comes from <see cref="SetPose"/>, measured on the received samples.
        /// It is deliberately NOT measured here from how far the body moved this frame:
        /// that reads the interpolation rather than the player, and interpolation advances
        /// then waits for the next packet, so the value alternates between moving and
        /// frozen at the packet rate. Observed as a body flickering between walk and run
        /// while sprinting, and sinking at the start and end of movement.
        /// </para>
        /// </summary>
        private void DriveAnimator(Vector3 newPosition, float deltaTime)
        {
            if (_animator == null || deltaTime <= 0f) return;

            try
            {
                // A teleport, scene load or respawn is not running. Anything past a sprint
                // is a discontinuity, so report nothing rather than letting one spike
                // poison the smoothed value for the next second.
                var instantaneous = _reportedSpeed > MaxBelievableSpeed ? 0f : _reportedSpeed;

                // Light smoothing only. The input is already stable, so this just softens
                // the step between one packet's speed and the next.
                _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, instantaneous,
                                            1f - Mathf.Exp(-8f * deltaTime));
                if (_smoothedSpeed < 0.05f) _smoothedSpeed = 0f;

                var gait = ChooseGait(_smoothedSpeed);
                _currentGait = gait;

                if (_idleHash != -1) _animator.SetFloat(_idleHash, gait == Gait.Idle ? 1f : 0f);
                if (_walkHash != -1) _animator.SetFloat(_walkHash, gait == Gait.Walk ? 1f : 0f);
                if (_sprintHash != -1) _animator.SetFloat(_sprintHash, gait == Gait.Run ? 1f : 0f);

                // Raw metres per second, NOT normalised.
                //
                // Normalising this was tried and made walking distinctly worse, feet
                // sinking into the pavement. The parameter scales playback within a gait
                // and expects real speed. The sinking was never this; it was blending two
                // gaits at once.
                // NEVER FEED A MOVING GAIT A NEAR-ZERO SPEED.
                //
                // Measured, and the relationship is almost exactly one to one below 0.9:
                //
                //   speed=0.88  hipOffset=0.788      speed=0.28  hipOffset=0.189
                //   speed=0.50  hipOffset=0.459      speed=0.15  hipOffset=-0.002
                //
                // The walk pose collapses toward the floor as Speed approaches zero,
                // taking the body with it, and springs back only when the gait finally
                // flips to Idle. That collapse is the whole "falls through the world when
                // starting and stopping" bug: it is worst exactly when crossing the
                // threshold, which is when speed is lowest. Above roughly 1.0 it
                // saturates, which is why steady walking always looked right.
                //
                // Idle does not use this parameter, so zero is safe there.
                if (_speedHash != -1)
                {
                    var speedParam = gait == Gait.Idle
                        ? 0f
                        : Mathf.Max(MinimumPoseSpeed, _smoothedSpeed);

                    _animator.SetFloat(_speedHash, speedParam);
                }

                // Keep every other gait out of the blend.
                foreach (var hash in _otherGaitHashes) _animator.SetFloat(hash, 0f);

                SampleStopDip(newPosition, deltaTime);

                // Report the speeds actually observed, so the walk and run thresholds can
                // be set from measurement instead of guesswork.
                if (_smoothedSpeed > _peakSpeed) _peakSpeed = _smoothedSpeed;

                _speedReportTimer -= deltaTime;
                if (_speedReportTimer <= 0f && _peakSpeed > 0.1f)
                {
                    _speedReportTimer = 5f;
                    NightsharePlugin.Logger?.LogInfo(
                        $"Avatar speed: now {_smoothedSpeed:0.00} m/s, peak {_peakSpeed:0.00} " +
                        $"(walk<{WalkSpeed:0.0} run>{SprintSpeed:0.0}) gait={_currentGait}");
                    NightshareLog.Write("INFO",
                        $"Avatar speed: now {_smoothedSpeed:0.00} peak {_peakSpeed:0.00} " +
                        $"gait={_currentGait}");
                    _peakSpeed = 0f;
                }
            }
            catch (Exception)
            {
                // A destroyed animator mid-frame is handled by IsAlive next tick.
            }
        }

        /// <summary>
        /// True for a lower detail LOD mesh, which should stay hidden.
        /// <para>
        /// Naming seen on the real rigs is <c>Body_LOD_0</c> (full detail) then
        /// <c>Body_LOD_001</c> to <c>Body_LOD_004</c>. Only the trailing number
        /// distinguishes them, and <c>_LOD_0</c> is the one to keep.
        /// </para>
        /// </summary>
        private static bool IsReducedLod(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            var at = name.LastIndexOf("_LOD_", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;

            var suffix = name.Substring(at + 5);
            if (suffix.Length == 0) return false;

            // "0" is full detail and stays. Anything else, including "001", does not.
            foreach (var c in suffix)
                if (c < '0' || c > '9') return false;

            return int.TryParse(suffix, out var level) && level != 0;
        }

        /// <summary>
        /// Record the received height and the actual height for a second after a stop, so
        /// the dip can be attributed rather than guessed at.
        /// </summary>
        private void SampleStopDip(Vector3 appliedPosition, float deltaTime)
        {
            // Off by default. It writes two dozen lines per gait change, which is far too
            // much for normal play, but it is what found the pose collapse and it should
            // stay available for the next animation question.
            if (!NightsharePlugin.Settings.LogGaitChanges.Value) return;

            // Trigger on any gait CHANGE, not just stopping. Steady walking and steady
            // running are both correct now; only the switch between states glitches, so
            // the transition is the thing worth watching.
            if (_currentGait != _lastSampledGait)
            {
                _dipSamplesLeft = 24;      // just over a second of samples
                _dipSampleTimer = 0f;

                NightshareLog.Write("INFO",
                    $"GAIT CHANGE: '{Name}' {_lastSampledGait} -> {_currentGait}. " +
                    $"Watching the hips and the animator state through the transition.");

                _lastSampledGait = _currentGait;
            }

            if (_dipSamplesLeft <= 0) return;

            _dipSampleTimer -= deltaTime;
            if (_dipSampleTimer > 0f) return;
            _dipSampleTimer = 0.05f;
            _dipSamplesLeft--;

            try
            {
                // The hips are the highest-authority bone for "is the body sinking",
                // because they move with the animation while the root does not.
                var hipsY = float.NaN;
                if (_animator != null && _animator.isHuman)
                {
                    var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
                    if (hips != null) hipsY = hips.position.y;
                }

                // Hip height RELATIVE to the root is the number that matters: an absolute
                // value confuses "the body sank" with "the whole avatar moved".
                var hipOffset = float.IsNaN(hipsY) ? float.NaN : hipsY - _transform.position.y;

                // What the state machine itself is doing. If it is mid-transition while
                // the hips drop, the animator is blending between states and the gait
                // floats are fighting its own transition; if it is settled and the hips
                // still drop, the pose being played is simply wrong.
                var stateLine = "state=?";
                try
                {
                    var info = _animator.GetCurrentAnimatorStateInfo(0);
                    var transitioning = _animator.IsInTransition(0);
                    stateLine = $"state={info.shortNameHash} t={info.normalizedTime % 1f:0.00} " +
                                $"transitioning={transitioning}";
                }
                catch (Exception) { }

                NightshareLog.Write("INFO",
                    $"  gait={_currentGait,-4} hipOffset={hipOffset:0.000}  " +
                    $"speed={_smoothedSpeed:0.00}  {stateLine}");
            }
            catch (Exception)
            {
                _dipSamplesLeft = 0;
            }
        }

        /// <summary>
        /// Place the body exactly. The caller has already interpolated, so there is
        /// nothing left to smooth here: smoothing an already smooth path only adds lag.
        /// </summary>
        /// <param name="speed">
        /// The sender's real speed in metres per second, measured from received samples.
        /// Passed in rather than derived here, because measuring how far the rendered body
        /// moved this frame gives a value that alternates between moving and frozen at the
        /// packet rate.
        /// </param>
        public void SetPose(Vector3 position, float yaw, float speed)
        {
            _targetPosition = position;
            _targetYaw = yaw;
            _reportedSpeed = speed;
            _hasTarget = true;
        }

        /// <summary>
        /// Move toward the latest known position.
        /// <para>
        /// Smoothed rather than snapped. Updates arrive 15 times a second, so snapping
        /// would read as a visible stutter; easing toward the target hides the gap between
        /// packets at the cost of being a fraction of a second behind, which nobody can
        /// perceive on another player's body.
        /// </para>
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!IsAlive || !_hasTarget) return;

            // Re-assert visibility about once a second. A culling pass that runs later
            // would otherwise hide the body permanently.
            _visibilityTimer -= deltaTime;
            if (_visibilityTimer <= 0f)
            {
                _visibilityTimer = 1f;
                ForceVisible();
            }

            try
            {
                // The pose is already interpolated; apply it directly. Easing toward it
                // again would re-introduce the stutter this was built to remove, because
                // a filter over a moving target always lags behind it.
                _transform.position = _targetPosition;
                _transform.rotation = Quaternion.Euler(0f, _targetYaw, 0f);

                DriveAnimator(_targetPosition, deltaTime);
            }
            catch (Exception)
            {
                // A destroyed transform mid-frame is handled by IsAlive next tick.
            }
        }

        public void Rename(string name)
        {
            Name = name;
            if (IsAlive) GameObject.name = $"Nightshare_RemotePlayer_{Peer.ToShortString()}_{name}";
        }

        public void Destroy()
        {
            if (_beacon != null)
            {
                try { UnityEngine.Object.Destroy(_beacon); } catch (Exception) { }
                _beacon = null;
            }

            if (GameObject == null) return;

            try { UnityEngine.Object.Destroy(GameObject); }
            catch (Exception) { }

            GameObject = null;
            _transform = null;
        }
    }
}
