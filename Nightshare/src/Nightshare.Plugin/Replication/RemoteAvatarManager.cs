using System;
using System.Collections.Generic;
using Nightshare.Core;
using UnityEngine;

namespace Nightshare.Replication
{
    /// <summary>
    /// Keeps one <see cref="RemoteAvatar"/> per remote player, in step with whatever the
    /// transform replicator last heard.
    /// <para>
    /// Spawning is separate from tracking on purpose. The position stream was proved
    /// correct on its own first, so when a body misbehaves the question is only ever about
    /// the body.
    /// </para>
    /// </summary>
    internal sealed class RemoteAvatarManager
    {
        private readonly Dictionary<PeerId, RemoteAvatar> _avatars = new();

        /// <summary>
        /// Wait this long between spawn attempts when no NPC is available to clone. Early
        /// in a world load there are none, and retrying every frame would spam the log.
        /// </summary>
        private const float RetrySeconds = 2f;

        private float _retryTimer;
        private bool _warnedNoSource;

        public bool Enabled { get; set; } = true;
        public int Count => _avatars.Count;

        public void Tick(PlayerTransformReplicator transforms, float deltaTime)
        {
            if (!Enabled || transforms == null) return;

            _retryTimer -= deltaTime;
            var maySpawn = _retryTimer <= 0f;

            foreach (var kv in transforms.Remotes)
            {
                var peer = kv.Key;
                var state = kv.Value;

                if (!_avatars.TryGetValue(peer, out var avatar) || !avatar.IsAlive)
                {
                    if (!maySpawn) continue;

                    avatar = RemoteAvatar.TryCreate(peer, state.Name, state.Position, state.Yaw);
                    if (avatar == null)
                    {
                        _retryTimer = RetrySeconds;
                        if (!_warnedNoSource)
                        {
                            _warnedNoSource = true;
                            NightsharePlugin.Logger?.LogInfo(
                                "Avatar: no NPC available to clone yet, retrying. " +
                                "This is normal until the world has populated.");
                        }
                        continue;
                    }

                    _warnedNoSource = false;
                    _avatars[peer] = avatar;
                }

                // Interpolate between the two most recent samples rather than handing the
                // avatar a target to chase. See RemotePlayerState.Sample.
                state.Sample(Time.unscaledTime, out var position, out var yaw);

                // Speed comes from the received samples, not from how far the rendered
                // body happened to move this frame. See RemotePlayerState.Speed.
                avatar.SetPose(position, yaw, state.Speed);
                avatar.Tick(deltaTime);
            }

            RemoveOrphans(transforms);
        }

        /// <summary>Drop avatars whose player is no longer being tracked.</summary>
        private void RemoveOrphans(PlayerTransformReplicator transforms)
        {
            if (_avatars.Count == 0) return;

            List<PeerId> gone = null;
            foreach (var kv in _avatars)
            {
                if (!transforms.Remotes.ContainsKey(kv.Key))
                    (gone ??= new List<PeerId>()).Add(kv.Key);
            }

            if (gone == null) return;
            foreach (var peer in gone) Remove(peer);
        }

        public void Remove(PeerId peer)
        {
            if (!_avatars.TryGetValue(peer, out var avatar)) return;

            avatar.Destroy();
            _avatars.Remove(peer);
            NightsharePlugin.Logger?.LogInfo($"Avatar: removed {peer.ToShortString()}");
        }

        public void Clear()
        {
            foreach (var kv in _avatars) kv.Value.Destroy();
            _avatars.Clear();
            _retryTimer = 0f;
            _warnedNoSource = false;
        }

        public void Describe(Action<string> log)
        {
            if (_avatars.Count == 0) { log("  avatars    : none"); return; }

            log($"  avatars    : {_avatars.Count}");
            foreach (var kv in _avatars)
            {
                var a = kv.Value;
                var where = a.IsAlive
                    ? $"at ({a.GameObject.transform.position.x:0.0}, " +
                      $"{a.GameObject.transform.position.y:0.0}, " +
                      $"{a.GameObject.transform.position.z:0.0})"
                    : "DEAD";
                log($"    {(string.IsNullOrEmpty(a.Name) ? a.Peer.ToShortString() : a.Name)} {where}");
            }
        }
    }
}
