using System;
using System.Collections.Generic;
using Nightshare.Core;
using Nightshare.Core.Protocol;
using Nightshare.Core.Session;
using Nightshare.Core.Transport;
using UnityEngine;

namespace Nightshare.Replication
{
    /// <summary>What we last heard about a remote player.</summary>
    internal sealed class RemotePlayerState
    {
        public PeerId Peer;
        public string Name = "";

        /// <summary>Where they were when the last update arrived.</summary>
        public Vector3 Position;
        public float Yaw;
        public bool IsMoving;

        /// <summary>
        /// The previous sample, kept so the avatar can be interpolated between the two
        /// rather than teleporting on each packet.
        /// </summary>
        public Vector3 PreviousPosition;
        public float PreviousYaw;

        /// <summary>Unscaled time when the latest sample arrived.</summary>
        public float ReceivedAt;

        /// <summary>
        /// Measured gap between the last two samples. Used to pace interpolation against
        /// what is actually arriving rather than against the nominal send rate, which
        /// drifts with frame rate and network timing.
        /// </summary>
        public float SampleInterval = 1f / 15f;

        public int UpdateCount;

        public void Accept(PlayerTransformV1 msg, float now)
        {
            PreviousPosition = Position;
            PreviousYaw = Yaw;

            if (UpdateCount > 0)
            {
                var gap = now - ReceivedAt;
                // Ignore absurd gaps from a stall or a pause, which would otherwise make
                // the avatar crawl for a second afterwards.
                if (gap > 0.001f && gap < 0.5f)
                    SampleInterval = Mathf.Lerp(SampleInterval, gap, 0.25f);
            }

            Position = new Vector3(msg.X, msg.Y, msg.Z);
            Yaw = msg.Yaw;
            IsMoving = msg.IsMoving;
            ReceivedAt = now;
            UpdateCount++;
        }

        /// <summary>
        /// Where the body should be drawn right now.
        /// <para>
        /// Proper interpolation between the last two received samples, rather than easing
        /// toward the newest one. Easing toward a target that jumps every 66 ms produces a
        /// visible stutter: the body accelerates after each packet and coasts to a halt
        /// before the next. Walking the line between two known samples at a constant rate
        /// is smooth, at the cost of drawing one packet in the past, which nobody can see.
        /// </para>
        /// </summary>
        public void Sample(float now, out Vector3 position, out float yaw)
        {
            var span = Mathf.Max(0.001f, SampleInterval);
            var t = Mathf.Clamp01((now - ReceivedAt) / span);

            position = Vector3.Lerp(PreviousPosition, Position, t);
            yaw = Mathf.LerpAngle(PreviousYaw, Yaw, t);
        }

        /// <summary>
        /// How fast the remote player is really moving, in metres per second.
        /// <para>
        /// <b>Measured from the received samples, never from the interpolated transform.</b>
        /// Deriving it from the rendered body gives a value that alternates between moving
        /// and frozen several times a second: interpolation advances at a constant rate
        /// until it reaches the newest sample, then waits for the next packet. That
        /// oscillation straddles the walk and run thresholds, which is a body flickering
        /// between two gaits, and it spikes at the start and end of movement, which is a
        /// body sinking through the ground.
        /// </para>
        /// <para>
        /// The distance between two samples over the interval between them is the sender's
        /// actual speed, and it is stable by construction.
        /// </para>
        /// </summary>
        public float Speed
        {
            get
            {
                if (UpdateCount < 2) return 0f;

                var moved = new Vector3(
                    Position.x - PreviousPosition.x,
                    0f,
                    Position.z - PreviousPosition.z).magnitude;

                return moved / Mathf.Max(0.001f, SampleInterval);
            }
        }
    }

    /// <summary>
    /// Sends where the local player is, and tracks where everyone else is.
    /// <para>
    /// This deliberately stops at tracking. Actually drawing a body for a remote player is
    /// a separate problem with its own failure modes, and keeping the two apart means the
    /// position stream can be proved correct (via the fake peer) before any avatar exists
    /// to confuse the picture.
    /// </para>
    /// <para>
    /// Topology is star shaped: clients send only to the host, and the host relays to
    /// everyone else. Clients are not peers of each other. This matches how the transport
    /// is built and avoids every client needing a connection to every other.
    /// </para>
    /// </summary>
    internal sealed class PlayerTransformReplicator
    {
        /// <summary>
        /// Updates per second. Fifteen is smooth once interpolated and costs about
        /// 500 bytes a second per player, which is nothing.
        /// </summary>
        public float SendRate { get; set; } = 15f;

        /// <summary>Below this, a player counts as standing still.</summary>
        private const float MovingThresholdSqr = 0.0004f;   // 2 cm per sample

        private readonly Dictionary<PeerId, RemotePlayerState> _remotes = new();

        private Nivalis.PlayerManager _playerManager;
        private float _sendAccumulator;
        private Vector3 _lastSentPosition;
        private bool _warnedNoPlayer;
        private bool _foundPlayerOnce;
        private Transform _yawSource;

        /// <summary>The child of the player object that carries yaw. Found by the rig probe.</summary>
        private const string RigChildName = "Rig";

        public int SentCount { get; private set; }
        public int ReceivedCount { get; private set; }

        /// <summary>
        /// The player object, or null before a world is loaded.
        /// <para>
        /// <b>The search behind this is throttled.</b> FindObjectOfType is a scene sweep,
        /// and this property is read from several places every frame. While it returns null
        /// (the whole of a load) an unthrottled version runs several sweeps per frame
        /// against a scene that is still being built, which is enough on its own to stall
        /// the load it is waiting for.
        /// </para>
        /// </summary>
        public GameObject LocalPlayerObject
        {
            get
            {
                try
                {
                    var manager = ResolvePlayerManager();
                    return manager?.LocalPlayer?.PlayerGameObject;
                }
                catch (Exception) { return null; }
            }
        }

        private float _nextPlayerSearch;

        /// <summary>
        /// The PlayerManager, searched for at most once a second while it does not exist.
        /// <para>
        /// Every caller goes through here. The unthrottled version ran a scene sweep per
        /// call, and the callers include a 15 Hz send path that is active throughout a
        /// guest's load, which stalled the load it was waiting on.
        /// </para>
        /// </summary>
        private Nivalis.PlayerManager ResolvePlayerManager()
        {
            if (_playerManager != null) return _playerManager;

            if (Time.unscaledTime < _nextPlayerSearch) return null;
            _nextPlayerSearch = Time.unscaledTime + 1f;

            try { _playerManager = UnityEngine.Object.FindObjectOfType<Nivalis.PlayerManager>(); }
            catch (Exception) { return null; }

            return _playerManager;
        }

        public IReadOnlyDictionary<PeerId, RemotePlayerState> Remotes => _remotes;

        public void Reset()
        {
            _remotes.Clear();
            _playerManager = null;
            _yawSource = null;      // the player object is rebuilt on a world load
            _foundPlayerOnce = false;
            _sendAccumulator = 0f;
            SentCount = 0;
            ReceivedCount = 0;
        }

        public void Forget(PeerId peer) => _remotes.Remove(peer);

        // ---------------------------------------------------------------- sending

        public void Tick(NightshareSession session, float deltaTime)
        {
            if (session == null || !session.IsActive || session.Peers.Count == 0) return;

            _sendAccumulator += deltaTime;
            var interval = 1f / Mathf.Max(1f, SendRate);
            if (_sendAccumulator < interval) return;
            _sendAccumulator = 0f;

            if (!TryReadLocalTransform(out var position, out var yaw)) return;

            var moving = (position - _lastSentPosition).sqrMagnitude > MovingThresholdSqr;
            _lastSentPosition = position;

            var msg = new PlayerTransformV1
            {
                Peer = session.LocalPeer,
                X = position.x,
                Y = position.y,
                Z = position.z,
                Yaw = yaw,
                IsMoving = moving,
            }.Serialise();

            try
            {
                if (session.IsHost)
                    session.Broadcast(msg, DeliveryMode.Unreliable);
                else
                    session.SendToHost(msg, DeliveryMode.Unreliable);

                SentCount++;

                // Log the first send and then a sample. A silent success is indistinguishable
                // from doing nothing at all, which has already cost two test rounds.
                if (SentCount == 1 || SentCount % 300 == 0)
                {
                    NightsharePlugin.Logger?.LogInfo(
                        $"Position: sent #{SentCount} from ({position.x:0.0}, {position.y:0.0}, {position.z:0.0})" +
                        $" yaw {yaw:0}{(moving ? " moving" : "")}");
                }
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Transform send failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- receiving

        /// <summary>
        /// Accept a transform. On the host this also relays it to the other clients, since
        /// clients never talk directly to each other.
        /// </summary>
        public void Apply(NightshareSession session, PeerId from, PlayerTransformV1 msg)
        {
            // Never let a peer move somebody else. The id in the payload must be its own.
            if (!session.IsHost && msg.Peer == session.LocalPeer) return;
            if (session.IsHost && msg.Peer != from)
            {
                NightsharePlugin.Logger?.LogWarning(
                    $"{from.ToShortString()} sent a transform claiming to be " +
                    $"{msg.Peer.ToShortString()}. Ignored.");
                return;
            }

            if (msg.Peer == session.LocalPeer) return;   // our own, echoed back

            if (!_remotes.TryGetValue(msg.Peer, out var state))
            {
                state = new RemotePlayerState { Peer = msg.Peer, Position = new Vector3(msg.X, msg.Y, msg.Z) };
                _remotes[msg.Peer] = state;

                foreach (var p in session.Peers)
                    if (p.Id == msg.Peer) state.Name = p.Name;

                NightsharePlugin.Logger?.LogInfo(
                    $"Tracking {(string.IsNullOrEmpty(state.Name) ? msg.Peer.ToShortString() : state.Name)} " +
                    $"at ({msg.X:0.0}, {msg.Y:0.0}, {msg.Z:0.0})");
            }

            state.Accept(msg, Time.unscaledTime);
            ReceivedCount++;

            // Star topology: the host is the only route between two clients.
            if (session.IsHost)
            {
                try { session.Broadcast(msg.Serialise(), DeliveryMode.Unreliable, except: from); }
                catch (Exception) { }
            }
        }

        // ---------------------------------------------------------------- local player

        /// <summary>
        /// Find the transform that actually carries the player's facing.
        /// <para>
        /// <b>It is not the root.</b> <c>PlayerCharacter(Clone)</c> moves but never
        /// rotates, so reading its yaw gives a constant. Measured with the rig probe: a
        /// three-turn spin produced 1139 degrees of travel on <c>Rig</c> and every rigid
        /// child, and 0 on the root. The first version of this read the root and reported
        /// a frozen 48 degrees through an entire walk.
        /// </para>
        /// <para>
        /// <c>Camera.main</c> measured identically and is the fallback, but the Rig child
        /// is preferred because the main camera changes during dialogue and photo mode.
        /// </para>
        /// </summary>
        private Transform ResolveYawSource(GameObject playerObject)
        {
            if (_yawSource != null) return _yawSource;

            var rig = playerObject.transform.Find(RigChildName);
            if (rig != null)
            {
                _yawSource = rig;
                NightsharePlugin.Logger?.LogInfo($"Position: reading facing from '{RigChildName}'");
                return _yawSource;
            }

            var cam = Camera.main;
            if (cam != null)
            {
                _yawSource = cam.transform;
                NightsharePlugin.Logger?.LogWarning(
                    $"Position: no '{RigChildName}' child on the player, falling back to Camera.main. " +
                    $"Facing may be wrong during dialogue or photo mode.");
                return _yawSource;
            }

            _yawSource = playerObject.transform;
            NightsharePlugin.Logger?.LogWarning(
                "Position: could not find a rotating transform, using the player root. " +
                "Facing will be constant. Re-run the rig probe (F5).");
            return _yawSource;
        }

        /// <summary>
        /// Read the local player's position and yaw.
        /// <para>
        /// Uses FindObjectOfType rather than the Singleton generic static, for the same
        /// reason as the clock: this game has two different Singleton base classes and
        /// generic statics through Il2CppInterop are fragile.
        /// </para>
        /// </summary>
        private bool TryReadLocalTransform(out Vector3 position, out float yaw)
        {
            position = default;
            yaw = 0f;

            try
            {
                var player = ResolvePlayerManager()?.LocalPlayer;
                var go = player?.PlayerGameObject;
                if (go == null)
                {
                    if (!_warnedNoPlayer)
                    {
                        _warnedNoPlayer = true;
                        NightsharePlugin.Logger?.LogInfo(
                            "No local player yet, so no position is being sent. " +
                            "This is normal on the main menu.");
                    }
                    return false;
                }

                if (_warnedNoPlayer || !_foundPlayerOnce)
                {
                    _warnedNoPlayer = false;
                    _foundPlayerOnce = true;
                    NightsharePlugin.Logger?.LogInfo(
                        $"Position: local player found at " +
                        $"({go.transform.position.x:0.0}, {go.transform.position.y:0.0}, {go.transform.position.z:0.0})");
                }

                position = go.transform.position;
                yaw = ResolveYawSource(go).eulerAngles.y;
                return true;
            }
            catch (Exception ex)
            {
                if (!_warnedNoPlayer)
                {
                    _warnedNoPlayer = true;
                    NightsharePlugin.Logger?.LogWarning($"Could not read the local player: {ex.Message}");
                }
                return false;
            }
        }
    }
}
