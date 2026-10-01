using System;
using System.Collections.Generic;
using Nightshare.Core;
using Nightshare.Core.Protocol;
using Nightshare.Core.Session;

namespace Nightshare.Replication
{
    /// <summary>
    /// Holds the night until everybody is in bed.
    /// <para>
    /// Sleeping is the only ordinary action that moves the world for every player at once,
    /// so it cannot stay a private decision. The agreed rule is the Stardew one: the day
    /// advances when everyone is asleep, and until then whoever went to bed first waits.
    /// </para>
    /// <para>
    /// The host arbitrates. A client asks and is told; it never advances its own clock,
    /// which is the same rule the clock replicator already follows.
    /// </para>
    /// </summary>
    internal sealed class SleepCoordinator
    {
        /// <summary>
        /// Set while the host is performing the agreed sleep, so the Harmony patch lets
        /// that one call through instead of intercepting it into another request.
        /// </summary>
        internal static bool Performing;

        private readonly HashSet<PeerId> _ready = new();
        private bool _localWantsSleep;
        private int _localTarget;

        /// <summary>
        /// The bed this player walked up to, kept so the interaction can be replayed
        /// verbatim once everybody is ready. Each player sleeps in their own bed.
        /// </summary>
        private Nivalis.IBed _localBed;

        public Nivalis.IBed LocalBed => _localBed;

        /// <summary>
        /// Where the bed is, so a player who wanders off can be taken out of the queue.
        /// <para>
        /// Without this, somebody can touch a bed, walk across the city, and then be
        /// dragged into a sleep sequence the moment the other player lies down. Staying by
        /// the bed is what "waiting to sleep" means.
        /// </para>
        /// </summary>
        private UnityEngine.Vector3 _localBedPosition;
        private bool _haveBedPosition;

        /// <summary>
        /// How far a waiting player may stray before they are no longer considered ready.
        /// Generous enough to shuffle about beside the bed, short enough that leaving the
        /// room counts as leaving.
        /// </summary>
        private const float MaxDistanceFromBed = 5f;

        /// <summary>Latest state from the host, for the on-screen prompt.</summary>
        public SleepStateV1 LastState { get; private set; }

        /// <summary>True while this player is in bed waiting for others.</summary>
        public bool LocalIsWaiting => _localWantsSleep;

        /// <summary>
        /// Who is at a bed and who is not, for the prompt.
        /// <para>
        /// The host knows this directly. A client only knows what the last SleepStateV1
        /// told it, which carries the names rather than ids precisely so the prompt can be
        /// drawn without the client needing its own view of the peer list.
        /// </para>
        /// </summary>
        public void DescribeWaiting(NightshareSession session,
                                    List<string> atBed, List<string> notAtBed)
        {
            atBed.Clear();
            notAtBed.Clear();

            if (session == null || !session.IsActive) return;

            if (session.IsHost)
            {
                (_ready.Contains(session.LocalPeer) ? atBed : notAtBed).Add("You");

                foreach (var p in session.Peers)
                    (_ready.Contains(p.Id) ? atBed : notAtBed).Add(p.Name);

                return;
            }

            // Client. The local player is in bed by definition if the prompt is up.
            atBed.Add("You");

            var state = LastState;
            if (state == null || string.IsNullOrEmpty(state.WaitingOn)) return;

            foreach (var name in state.WaitingOn.Split(','))
            {
                var trimmed = name.Trim();
                if (trimmed.Length == 0) continue;

                // The host writes "you" for itself, which means the host from our side.
                notAtBed.Add(trimmed == "you" ? session.HostName : trimmed);
            }
        }

        public void Reset()
        {
            _ready.Clear();
            _localWantsSleep = false;
            _localTarget = 0;
            LastState = null;
            Performing = false;
        }

        // ---------------------------------------------------------------- local intent

        /// <summary>
        /// The local player tried to sleep. Returns true if the game should be allowed to
        /// sleep right now, false if it must wait for the others.
        /// </summary>
        public bool RequestSleep(NightshareSession session, Nivalis.IBed bed, int targetGameSeconds)
        {
            // Alone, or not in a session at all: sleep is nobody else's business.
            if (session == null || !session.IsActive || session.Peers.Count == 0) return true;

            _localWantsSleep = true;
            _localBed = bed;
            _localTarget = targetGameSeconds;
            RememberBedPosition(bed);

            if (session.IsHost)
            {
                _ready.Add(session.LocalPeer);
                return EvaluateOnHost(session);
            }

            session.SendToHost(new SleepRequestV1
            {
                WantsToSleep = true,
                TargetGameSeconds = targetGameSeconds,
            }.Serialise());

            NightsharePlugin.Logger?.LogInfo("Sleep: waiting for the other player(s)");
            return false;
        }

        /// <summary>
        /// Note where the bed is. IBed is an interface, but every implementation is a
        /// MonoBehaviour, so the transform is one cast away.
        /// </summary>
        private void RememberBedPosition(Nivalis.IBed bed)
        {
            _haveBedPosition = false;
            if (bed == null) return;

            try
            {
                var component = bed.TryCast<UnityEngine.Component>();
                if (component == null || component.transform == null) return;

                _localBedPosition = component.transform.position;
                _haveBedPosition = true;
            }
            catch (Exception)
            {
                // Without a position the distance check simply does not run, which is the
                // old behaviour rather than a broken one.
            }
        }

        /// <summary>
        /// Take a waiting player out of the queue if they have walked away from their bed.
        /// Call every frame while waiting; does nothing otherwise.
        /// </summary>
        public void CheckStillAtBed(NightshareSession session, UnityEngine.Vector3 playerPosition)
        {
            if (!_localWantsSleep || !_haveBedPosition) return;

            if ((playerPosition - _localBedPosition).sqrMagnitude <=
                MaxDistanceFromBed * MaxDistanceFromBed)
            {
                return;
            }

            NightsharePlugin.Logger?.LogInfo(
                "Sleep: walked away from the bed, no longer waiting to sleep");
            CancelSleep(session);
        }

        /// <summary>The local player got out of bed before everyone was ready.</summary>
        public void CancelSleep(NightshareSession session)
        {
            if (!_localWantsSleep) return;

            _localWantsSleep = false;
            _localBed = null;
            _haveBedPosition = false;

            if (session == null || !session.IsActive) return;

            if (session.IsHost)
            {
                _ready.Remove(session.LocalPeer);
                BroadcastState(session);
            }
            else
            {
                session.SendToHost(new SleepRequestV1 { WantsToSleep = false }.Serialise());
            }

            NightsharePlugin.Logger?.LogInfo("Sleep: no longer waiting");
        }

        // ---------------------------------------------------------------- host side

        public void OnRequest(NightshareSession session, PeerId from, SleepRequestV1 msg)
        {
            if (session == null || !session.IsHost) return;

            if (msg.WantsToSleep)
            {
                _ready.Add(from);
                if (msg.TargetGameSeconds > _localTarget) _localTarget = msg.TargetGameSeconds;
            }
            else
            {
                _ready.Remove(from);
            }

            EvaluateOnHost(session);
        }

        /// <summary>
        /// Decide whether everyone is in bed. Returns true when the sleep may proceed.
        /// </summary>
        private bool EvaluateOnHost(NightshareSession session)
        {
            var total = session.Peers.Count + 1;   // peers plus the host
            var ready = _ready.Count;

            BroadcastState(session);

            if (ready < total)
            {
                NightsharePlugin.Logger?.LogInfo($"Sleep: {ready} of {total} in bed, waiting");
                return false;
            }

            NightsharePlugin.Logger?.LogInfo($"Sleep: all {total} in bed, advancing the day");
            return true;
        }

        private void BroadcastState(NightshareSession session)
        {
            var total = session.Peers.Count + 1;

            var waiting = new List<string>();
            if (!_ready.Contains(session.LocalPeer)) waiting.Add("you");
            foreach (var p in session.Peers)
                if (!_ready.Contains(p.Id)) waiting.Add(p.Name);

            var state = new SleepStateV1
            {
                ReadyCount = _ready.Count,
                TotalCount = total,
                WaitingOn = string.Join(", ", waiting),
            };

            LastState = state;
            session.Broadcast(state.Serialise());
        }

        /// <summary>
        /// Everyone is in bed and the host has slept. Tell the clients where time landed.
        /// </summary>
        public void AnnounceDayAdvanced(NightshareSession session, int totalGameSeconds, int day)
        {
            _ready.Clear();
            _localWantsSleep = false;
            LastState = null;

            session?.Broadcast(new DayAdvancedV1
            {
                TotalGameSeconds = totalGameSeconds,
                GameplayGameDay = day,
            }.Serialise());
        }

        /// <summary>The agreed wake time, which is the latest anybody asked for.</summary>
        public int AgreedTarget => _localTarget;

        // ---------------------------------------------------------------- client side

        public void OnState(SleepStateV1 state)
        {
            LastState = state;

            if (state.ReadyCount < state.TotalCount && !string.IsNullOrEmpty(state.WaitingOn))
            {
                NightsharePlugin.Logger?.LogInfo(
                    $"Sleep: {state.ReadyCount} of {state.TotalCount} in bed, waiting for {state.WaitingOn}");
            }
        }

        /// <summary>The host says everybody is ready. Stop waiting and lie down.</summary>
        public void OnProceed()
        {
            LastState = null;
            NightsharePlugin.Logger?.LogInfo("Sleep: everybody is at a bed, laying down");
        }

        public void OnDayAdvanced()
        {
            _localWantsSleep = false;
            _ready.Clear();
            LastState = null;
            NightsharePlugin.Logger?.LogInfo("Sleep: everyone slept, a new day");
        }
    }
}
