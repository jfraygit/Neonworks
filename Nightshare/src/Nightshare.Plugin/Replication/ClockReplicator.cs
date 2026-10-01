using System;
using Nightshare.Core.Protocol;
using Nightshare.Core.Session;
using UnityEngine;

namespace Nightshare.Replication
{
    /// <summary>
    /// Makes the host's clock the only clock.
    /// <para>
    /// The whole of Nivalis Nights time is one integer: <c>TimeStamp</c> wraps
    /// <c>TotalGameSeconds</c> and derives day, hour, minute, weekday and part of day from
    /// it. So the wire format is that integer and nothing else.
    /// </para>
    /// <para>
    /// <b>A client runs its own clock and is corrected forward.</b> It is never suppressed.
    /// An earlier version did suppress it, with a Harmony prefix on
    /// <c>TimeOfDayManager.Update</c>, and that deadlocked every load; see the note below,
    /// which is the single most expensive lesson in this file. Both clocks tick at the same
    /// rate, so the correction is usually zero.
    /// </para>
    /// </summary>
    internal sealed class ClockReplicator
    {
        // THE CLIENT'S CLOCK IS NO LONGER SUPPRESSED, AND MUST NOT BE AGAIN.
        //
        // There was a Harmony prefix on TimeOfDayManager.Update that stopped a client
        // running its own time, on the reasoning that two clocks advancing independently
        // would drift. It cost two days.
        //
        // The game needs time to pass in order to finish a load. Freezing it deadlocks:
        // the loading sequence waits for the clock, the clock waits for the world. It
        // first showed up as a guest stuck on a loading screen whose world had in fact
        // loaded perfectly, and then again on zone travel, because travelling is itself a
        // load. Every place the game loads anything is another instance of the same trap.
        //
        // Correction alone is enough. The host broadcasts its time once a second and the
        // client nudges forward by the difference; if both tick at the same rate that
        // difference is zero and nothing happens. Worst case the client is a fraction of a
        // second out between corrections, which nobody can perceive, and which is a trade
        // worth making to never fight the game for control of its own clock.

        private Nivalis.TimeOfDayManager _manager;
        private float _secondsSinceBroadcast;
        private int _lastSentSeconds = -1;
        private bool _warnedNoManager;
        private float _nextManagerSearch;

        /// <summary>How often the host broadcasts, in real seconds.</summary>
        public float BroadcastInterval { get; set; } = 1.0f;

        /// <summary>
        /// Counters so a session can be verified from the log alone.
        /// <para>
        /// Added after a test where the clock could not be confirmed either way, because
        /// per-message logging is too noisy to leave on and there was nothing else to look
        /// at. A counter costs nothing and turns "did it work" into a fact.
        /// </para>
        /// </summary>
        public int BroadcastCount { get; private set; }

        public int AppliedCount { get; private set; }

        /// <summary>Log a sample line every N broadcasts so the log shows movement, not spam.</summary>
        private const int LogEvery = 60;

        public void Reset()
        {
            // Before anything else. Reset is called on leaving a session and before the
            // host's world is loaded, and a pause lock that outlives either would freeze a
            // player with no way to work out why.
            ReleasePause("session reset");

            _manager = null;
            _secondsSinceBroadcast = 0f;
            _lastSentSeconds = -1;
            BroadcastCount = 0;
            AppliedCount = 0;
        }

        // ---------------------------------------------------------------- host

        /// <summary>Broadcast the clock on an interval. Call every frame on the host.</summary>
        public void HostTick(NightshareSession session, float deltaTime)
        {
            if (session == null || !session.IsActive || session.Peers.Count == 0) return;

            _secondsSinceBroadcast += deltaTime;
            if (_secondsSinceBroadcast < BroadcastInterval) return;
            _secondsSinceBroadcast = 0f;

            int seconds, day;
            bool paused;
            try
            {
                seconds = Nivalis.TimeOfDayManager.TotalGameSeconds;
                day = Nivalis.TimeOfDayManager.GameplayGameDay;

                // Instance property, unlike the statics above.
                var manager = ResolveManager();
                paused = manager != null && manager.IsPaused;
            }
            catch (Exception)
            {
                return;   // world not up yet
            }

            // BROADCAST EVEN WHEN THE CLOCK HAS NOT MOVED.
            //
            // This used to return early when the time was unchanged, on the reasoning that
            // there was nothing to say. A paused host is exactly the case where the time
            // stops changing, so the effect was that pausing made the host go SILENT: the
            // guest was never told, kept running its own clock, and drifted ahead for the
            // whole pause. On unpause the host's time was then behind the guest's, which
            // looked like time running backwards and logged a spurious "needs a world
            // resync" warning.
            //
            // The pause flag is state, not an event, so it has to keep being sent while it
            // is true. One small message a second is not worth optimising away.
            _lastSentSeconds = seconds;

            try
            {
                session.Broadcast(new ClockSyncV1
                {
                    TotalGameSeconds = seconds,
                    GameplayGameDay = day,
                    ClockHour = Nivalis.TimeOfDayManager.ClockHour,
                    ClockMinute = Nivalis.TimeOfDayManager.ClockMinute,
                    IsPaused = paused,
                }.Serialise());

                BroadcastCount++;

                // First one proves it started; then a sample often enough to show movement
                // without flooding the log.
                if (BroadcastCount == 1 || BroadcastCount % LogEvery == 0)
                {
                    // Also to the per-process log. Two instances share BepInEx's
                    // LogOutput.log and only the first can hold it open, so a host's clock
                    // lines were invisible in exactly the two-instance tests that need them.
                    var line = $"Clock: sent #{BroadcastCount}, day {day} " +
                               $"{Nivalis.TimeOfDayManager.ClockHour:00}:" +
                               $"{Nivalis.TimeOfDayManager.ClockMinute:00}" +
                               (paused ? " (paused)" : "");

                    NightsharePlugin.Logger?.LogInfo(line);
                    NightshareLog.Write("INFO", line);
                }
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Clock broadcast failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- client

        /// <summary>Apply the host's clock. Called when a ClockSyncV1 arrives.</summary>
        public void Apply(ClockSyncV1 msg)
        {
            var manager = ResolveManager();
            if (manager == null) return;

            // Mirror the host's pause first. The host's simulation is the world, so if it
            // stops, the world stops; a guest walking around in a frozen city would be
            // reading shop hours, curfew and NPC schedules the host's world does not agree
            // with. Done before the time correction because a paused guest should not then
            // be nudged.
            MirrorPause(msg.IsPaused);

            try
            {
                var local = Nivalis.TimeOfDayManager.TotalGameSeconds;
                var delta = msg.TotalGameSeconds - local;

                if (delta == 0) return;

                if (delta > 0)
                {
                    // Move forward through the game's own code path so day change, hour
                    // tick and minute tick all fire naturally. Forcing the value instead
                    // would leave every listener unaware that time had moved.
                    manager.AddTime(delta);
                }
                else
                {
                    // A small step backwards is ordinary and not worth a word. There is
                    // always a sub-second window between the host pausing and the guest
                    // being told, and the guest's own clock keeps ticking through it, so
                    // the guest ends up marginally ahead. The game has no rewind, so the
                    // guest simply waits for the host to catch up.
                    //
                    // A LARGE step backwards is different: it means the host reloaded, and
                    // no amount of clock nudging fixes that.
                    if (-delta >= BackwardsJumpIsAReload)
                    {
                        NightsharePlugin.Logger?.LogWarning(
                            $"Host clock went backwards by {-delta}s (host {msg.TotalGameSeconds}, " +
                            $"local {local}). Ignoring; this needs a world resync, not a clock nudge.");
                    }
                    return;
                }

                // Recompute ClockHour, ClockMinute, CurrentDayOfWeek and friends from the
                // new value. Cheap and idempotent.
                manager.UpdateStaticVariables();

                AppliedCount++;
                if (AppliedCount == 1 || AppliedCount % LogEvery == 0)
                {
                    NightsharePlugin.Logger?.LogInfo(
                        $"Clock: applied #{AppliedCount}, day {msg.GameplayGameDay} " +
                        $"{msg.ClockHour:00}:{msg.ClockMinute:00} (+{delta}s)");
                }
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Could not apply the host clock: {ex.Message}");
            }
        }

        /// <summary>The live manager, or null before the world is up.</summary>
        public Nivalis.TimeOfDayManager Manager => ResolveManager();

        // ---------------------------------------------------------------- pause

        /// <summary>
        /// Backwards steps smaller than this are the ordinary consequence of a sub-second
        /// gap between the host pausing and the guest hearing about it. At or above it, the
        /// host has reloaded and the clock is not the thing to fix.
        /// </summary>
        private const int BackwardsJumpIsAReload = 60;

        private Nivalis.OverrideableBool.OverrideLock _pauseLock;

        /// <summary>
        /// Our identity as a pause owner.
        /// <para>
        /// <c>TimeOfDayManager.Pause</c> takes an <c>Il2CppSystem.Object</c>, not a managed
        /// one, so <c>this</c> cannot be passed. The game keeps owners in a
        /// <c>HashSet&lt;object&gt;</c> and releases by identity, so this must be <b>one
        /// stable instance</b> reused for the lifetime of the replicator. A fresh object per
        /// pause would register a new owner each time and leak a permanent override, leaving
        /// the world paused forever.
        /// </para>
        /// <para>
        /// Created lazily rather than in a field initialiser, because the IL2CPP domain is
        /// not necessarily up when this type is first touched.
        /// </para>
        /// </summary>
        private Il2CppSystem.Object _pauseOwner;

        /// <summary>True while this guest is holding the world paused for the host.</summary>
        public bool IsMirroringHostPause => _pauseLock != null && !_pauseLock.IsReleased;

        /// <summary>
        /// Hold or release the game's own pause so the guest matches the host.
        /// <para>
        /// <b>This is the game pausing itself, not us fighting its clock.</b> The
        /// distinction matters enormously here. An earlier attempt at clock control used a
        /// Harmony prefix to suppress <c>TimeOfDayManager.Update</c> and deadlocked every
        /// load, because the game needs time to pass in order to finish loading.
        /// <c>TimeOfDayManager.Pause</c> is the supported mechanism the game uses itself
        /// whenever a menu opens: an owner-keyed lock on an <c>OverrideableBool</c>, which
        /// is reference counted across owners, so ours cannot stamp on anyone else's.
        /// </para>
        /// </summary>
        private void MirrorPause(bool hostIsPaused)
        {
            try
            {
                if (hostIsPaused)
                {
                    if (IsMirroringHostPause) return;

                    _pauseOwner ??= new Il2CppSystem.Object();
                    _pauseLock = Nivalis.TimeOfDayManager.Pause(_pauseOwner);
                    NightsharePlugin.Logger?.LogInfo("Clock: the host paused, holding the world");
                    NightshareLog.Write("INFO", "Clock: the host paused, holding the world");
                }
                else
                {
                    if (!IsMirroringHostPause) return;

                    ReleasePause("the host resumed");
                }
            }
            catch (Exception ex)
            {
                // A pause we cannot take is a cosmetic problem; a throw here would stop the
                // clock being applied at all, which is not.
                NightsharePlugin.Logger?.LogWarning($"Could not mirror the host's pause: {ex.Message}");
            }
        }

        /// <summary>
        /// Let go of the pause, unconditionally.
        /// <para>
        /// <b>Call this before anything that loads, and on leaving a session.</b> Holding
        /// time still across a load is the exact shape of the bug that cost two days: the
        /// load waits for the clock and the clock waits for the load. The game releases its
        /// own menu pause before loading for the same reason, and a lock of ours outliving
        /// the session it belonged to would leave a player frozen in their own single
        /// player game with nothing on screen to explain it.
        /// </para>
        /// </summary>
        public void ReleasePause(string why)
        {
            if (_pauseLock == null) return;

            try
            {
                if (!_pauseLock.IsReleased) _pauseLock.Release();

                // DID IT ACTUALLY RESUME?
                //
                // Releasing our own override is not the same as time running again: the
                // pause is reference counted across owners, so anyone else still holding one
                // keeps the world stopped. Reported here because a released lock and a
                // frozen clock look identical from the outside, and a log line saying
                // "released" while the player sits in a stopped world is worse than no log
                // line at all. LogOwners is the game's own diagnostic and names who is left.
                var stillPaused = "?";
                try
                {
                    var m = ResolveManager();
                    if (m != null)
                    {
                        stillPaused = m.IsPaused.ToString();
                        if (m.IsPaused) m.IsPausedOverrideableBool?.LogOwners();
                    }
                }
                catch (Exception) { }

                var line = $"Clock: released the world pause ({why}); still paused: {stillPaused}";
                NightsharePlugin.Logger?.LogInfo(line);
                NightshareLog.Write("INFO", line);
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Could not release the pause: {ex.Message}");
            }
            finally
            {
                _pauseLock = null;
            }
        }

        /// <summary>
        /// Apply a day advance, which is a jump rather than a tick.
        /// <para>
        /// Separate from <see cref="Apply"/> because that path refuses to move time
        /// backwards and nudges forward with AddTime, which is right for a clock that
        /// drifts by a second and wrong for a night that skips eight hours. Sleeping can
        /// also wrap past midnight, where the raw seconds go down while the day goes up.
        /// </para>
        /// </summary>
        public void ApplyDayAdvance(DayAdvancedV1 msg)
        {
            var manager = ResolveManager();
            if (manager == null) return;

            try
            {
                var local = Nivalis.TimeOfDayManager.TotalGameSeconds;
                var delta = msg.TotalGameSeconds - local;

                if (delta > 0)
                {
                    manager.AddTime(delta);
                }
                else
                {
                    // Wrapped past midnight. Go forward to the same clock time on the
                    // next day rather than trying to rewind.
                    manager.AddTime(delta + Nivalis.TimeOfDayManager.SECONDS_IN_DAY);
                }

                manager.UpdateStaticVariables();
                AppliedCount++;

                NightsharePlugin.Logger?.LogInfo(
                    $"Clock: day advanced to {msg.GameplayGameDay}, now at {msg.TotalGameSeconds}s");
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Could not apply the day advance: {ex.Message}");
            }
        }

        /// <summary>
        /// Find and cache the manager.
        /// <para>
        /// Deliberately not <c>Singleton&lt;TimeOfDayManager&gt;.instance</c>: there are two
        /// different Singleton base classes in this game and reaching a generic static
        /// through Il2CppInterop is fragile. FindObjectOfType is slower but unambiguous,
        /// and it only runs when the cache is empty.
        /// </para>
        /// </summary>
        private Nivalis.TimeOfDayManager ResolveManager()
        {
            if (_manager != null) return _manager;

            // Throttled. FindObjectOfType is a scene sweep and this is reached on every
            // clock message, so an unthrottled version sweeps repeatedly through a load.
            if (UnityEngine.Time.unscaledTime < _nextManagerSearch) return null;
            _nextManagerSearch = UnityEngine.Time.unscaledTime + 1f;

            try
            {
                _manager = UnityEngine.Object.FindObjectOfType<Nivalis.TimeOfDayManager>();
            }
            catch (Exception ex)
            {
                if (!_warnedNoManager)
                {
                    _warnedNoManager = true;
                    NightsharePlugin.Logger?.LogWarning($"Could not find TimeOfDayManager: {ex.Message}");
                }
                return null;
            }

            if (_manager == null) return null;

            _warnedNoManager = false;
            NightsharePlugin.Logger?.LogInfo("Clock: found TimeOfDayManager");
            return _manager;
        }
    }
}
