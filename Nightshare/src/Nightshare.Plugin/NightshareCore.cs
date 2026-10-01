using System;
using System.Collections.Generic;
using System.IO;
using Nightshare.Core;
using Nightshare.Core.Diagnostics;
using Nightshare.Core.Protocol;
using Nightshare.Core.Session;
using Nightshare.Core.Transport;
using Nightshare.Replication;

namespace Nightshare
{
    /// <summary>
    /// All of the mod's logic, in a plain class.
    /// <para>
    /// <b>This is deliberately NOT a MonoBehaviour.</b> Il2CppInterop registers the methods
    /// of an injected MonoBehaviour into the IL2CPP domain, and any signature mentioning a
    /// plain .NET type it cannot marshal produces a warning like
    /// <c>"has unsupported return type NightshareSession"</c>. Those warnings are harmless
    /// but they bury real ones. Keeping every signature that touches our own types off the
    /// MonoBehaviour removes the whole category.
    /// </para>
    /// <para>
    /// <see cref="NightshareRunner"/> is the thin Unity shell that drives this.
    /// </para>
    /// </summary>
    internal sealed class NightshareCore
    {
        public static readonly NightshareCore Instance = new NightshareCore();

        private NightshareSession _session;
        private readonly ClockReplicator _clock = new ClockReplicator();
        private readonly PlayerTransformReplicator _transforms = new PlayerTransformReplicator();
        private readonly PlayerRigProbe _rigProbe = new PlayerRigProbe();
        private readonly RemoteAvatarManager _avatars = new RemoteAvatarManager();
        private readonly SleepCoordinator _sleep = new SleepCoordinator();
        private readonly ZoneCoordinator _zones = new ZoneCoordinator();
        private PacketRecorder _recorder;
        private bool _startupHandled;

        private static NightshareConfig Config => NightsharePlugin.Settings;

        // Command line beats the config file, because two instances on one machine share a
        // single config directory and could not otherwise be given different roles.
        private static StartupMode EffectiveMode => CommandLineOverrides.Mode ?? Config.Mode.Value;
        private static string EffectiveEndpoint => CommandLineOverrides.Endpoint ?? Config.Endpoint.Value;
        private static string EffectivePlayerName => CommandLineOverrides.PlayerName ?? Config.PlayerName.Value;
        private static bool EffectiveRecordTraces => CommandLineOverrides.RecordTraces ?? Config.RecordTraces.Value;

        // ---------------------------------------------------------------- lifecycle

        public void Initialise()
        {
            _session = new NightshareSession
            {
                GameSecondsProvider = ReadGameSeconds,
                GameDayProvider = ReadGameDay,
            };

            _session.Log += m => { if (Config.VerboseLogging.Value) Log($"  {m}"); };
            _session.PeerJoined += OnPeerJoined;
            _session.PeerLeft += (p, why) =>
            {
                _transforms.Forget(p.Id);
                _avatars.Remove(p.Id);
                Log($"{p.Name} left: {why}");
            };
            _session.SessionFailed += OnSessionFailed;
            _session.SessionEstablished += OnSessionEstablished;
            _session.MessageReceived += OnMessageReceived;
        }

        public void Tick(float deltaTime)
        {
            try
            {
                _session?.Pump();
            }
            catch (Exception ex)
            {
                LogError($"Pump threw: {ex}");
            }

            // NOTHING REPLICATES UNTIL THIS MACHINE HAS A WORLD.
            //
            // A client connects from the main menu and stays connected right through its
            // load, so every replication system would otherwise run against a scene that is
            // still being built. Each of them searches for something that does not exist
            // yet, each search sweeps the whole scene, and together they stall the load
            // indefinitely. Observed as a guest stuck on the loading screen forever, with no
            // error anywhere to explain it.
            //
            // Worst of these was avatar spawning: the host is already sending positions, so
            // the guest tries to clone an NPC during the load, which deactivates a live
            // character and runs DestroyImmediate over its hierarchy.
            //
            // The session itself keeps pumping, so the connection survives the load. Nothing
            // else runs. This is also what the lobby join flow will make structural rather
            // than conditional: see docs/joining-a-session.md.
            // NOTHING CONNECTS, AND NOTHING REPLICATES, UNTIL THIS MACHINE HAS A WORLD.
            //
            // HandleStartup used to run here, above the return, so a launch script's
            // --nightshare-mode took effect at the MAIN MENU. Every awkward bug in this
            // area came from that one placement: travelling with no world, a snapshot
            // arriving before any manager existed, a frozen clock deadlocking the load,
            // inbound messages acted on mid-load. Each got its own guard.
            //
            // Connecting only once a world exists deletes the whole class instead, and it
            // is what the lobby flow makes structural: see docs/joining-a-session.md.
            if (!WorldIsLoaded) return;

            HandleStartup();

            // Only the host sends a clock. A client never does, and never simulates one.
            if (_session != null && _session.IsHost)
            {
                try { _clock.HostTick(_session, deltaTime); }
                catch (Exception ex) { LogError($"Clock tick threw: {ex.Message}"); }
            }

            // Both sides send their own position.
            try { _transforms.Tick(_session, deltaTime); }
            catch (Exception ex) { LogError($"Transform tick threw: {ex.Message}"); }

            try { _rigProbe.Tick(deltaTime); }
            catch (Exception ex) { LogError($"Rig probe threw: {ex.Message}"); }

            // A player who touched a bed and then wandered off stops counting as ready,
            // so nobody gets dragged into a sleep from across the city.
            if (_sleep.LocalIsWaiting)
            {
                try
                {
                    var player = _transforms.LocalPlayerObject;
                    if (player != null)
                        _sleep.CheckStillAtBed(_session, player.transform.position);
                }
                catch (Exception ex) { LogError($"Sleep proximity check threw: {ex.Message}"); }
            }

            try { UpdateSleepPrompt(); }
            catch (Exception ex) { LogError($"Sleep prompt threw: {ex.Message}"); }

            try { UpdatePausedNotice(); }
            catch (Exception ex) { LogError($"Paused notice threw: {ex.Message}"); }

            try { ReportArrivalIfPending(deltaTime); }
            catch (Exception ex) { LogError($"Arrival report threw: {ex.Message}"); }

            // The city is 53 scenes. A guest in a zone the host is not in is sealed inside
            // unloaded geometry, so they travel to wherever the host is.
            try
            {
                _zones.HostTick(_session);
                _zones.ClientTick(_session, worldIsLoaded: true, deltaTime);
            }
            catch (Exception ex) { LogError($"Zone sync threw: {ex.Message}"); }

            try { MaybeRequestWorld(deltaTime); }
            catch (Exception ex) { LogError($"Snapshot request threw: {ex.Message}"); }

            // Announce a day advance one frame late, so the game has finished moving time
            // before the new value is read and sent.
            if (_announceDayPending)
            {
                try { AnnounceDay(); }
                catch (Exception ex) { LogError($"Day announcement threw: {ex.Message}"); }
            }

            // Give every tracked remote player a body.
            try { _avatars.Tick(_transforms, deltaTime); }
            catch (Exception ex) { LogError($"Avatar tick threw: {ex.Message}"); }
        }

        public void Shutdown()
        {
            try { _recorder?.Dispose(); } catch { }

            // Resets the clock and, with it, releases any pause we were holding. A lock that
            // outlived the plugin would leave the player frozen with nothing to explain it.
            try { _clock.Reset(); } catch { }
            try { _session?.Dispose(); } catch { }

            // Both panels are DontDestroyOnLoad, so without this they survive the plugin.
            try { _sleepPrompt.Destroy(); } catch { }
            try { _pausedNotice.Destroy(); } catch { }
        }

        /// <summary>
        /// Honour the configured startup mode once. This is how the launch scripts bring up
        /// a host and a client without anybody pressing a key.
        /// </summary>
        private void HandleStartup()
        {
            if (_startupHandled) return;
            _startupHandled = true;

            switch (EffectiveMode)
            {
                case StartupMode.Host: StartHosting(); break;
                case StartupMode.Join: StartJoining(); break;
            }
        }

        // ---------------------------------------------------------------- session control

        public void StartHosting()
        {
            if (IsBusy()) return;

            Log($"Hosting on {EffectiveEndpoint} as '{EffectivePlayerName}'");
            BeginRecording(isHost: true);
            _session.Host(CreateTransport(), EffectiveEndpoint, BuildIdentity());
        }

        public void StartJoining()
        {
            if (IsBusy()) return;

            Log($"Joining {EffectiveEndpoint} as '{EffectivePlayerName}'");
            BeginRecording(isHost: false);
            _session.Join(CreateTransport(), EffectiveEndpoint, BuildIdentity());
        }

        public void Leave()
        {
            if (_session == null) return;

            // Hand the clock back first, or a client that leaves is frozen in time forever
            // with no obvious cause.
            _clock.Reset();
            Replication.LoadPauseGuard.Forget();
            _avatars.Clear();
            _transforms.Reset();
            _worldRequested = false;
            _worldReadyCheckTimer = 0f;
            _zones.Reset();
            _session.Leave();

            try { _recorder?.Dispose(); } catch { }
            _recorder = null;

            Log("Left the session");
        }

        private bool IsBusy()
        {
            if (_session == null) return true;

            if (_session.State is SessionState.Active
                              or SessionState.Connecting
                              or SessionState.Handshaking)
            {
                LogWarning($"Already in a session ({_session.State}). Leave first.");
                return true;
            }
            return false;
        }

        private INetTransport CreateTransport()
        {
            if (Config.Transport.Value == TransportChoice.SteamP2P)
                LogWarning("SteamP2P is not implemented yet. Falling back to Tcp.");

            return new TcpTransport();
        }

        private SessionIdentity BuildIdentity()
        {
            var buildId = Config.AllowGameVersionMismatch.Value ? "ignored" : GameFingerprint.Current;

            if (Config.AllowGameVersionMismatch.Value)
            {
                LogWarning("AllowGameVersionMismatch is on. Peers on different game builds " +
                           "can connect and may corrupt each other's state.");
            }
            else if (GameFingerprint.IsUnknown)
            {
                LogWarning("Could not fingerprint the game build. Joins will be refused.");
            }

            return new SessionIdentity
            {
                PlayerName = EffectivePlayerName,
                ModVersion = NightsharePlugin.Version,
                GameBuildId = buildId,
            };
        }

        // ---------------------------------------------------------------- events

        /// <summary>
        /// A peer completed its handshake. The host answers with the world.
        /// </summary>
        private void OnPeerJoined(RemotePeer peer)
        {
            Log($"{peer.Name} joined");

            // The world is NOT sent here. A client connects from the main menu with no
            // managers in existence, so a snapshot sent now has nothing to apply to. It
            // asks once its own world is loaded; see WorldSnapshotRequestV1.
        }

        /// <summary>
        /// Ship the world to a joining player: a header, then one message per manager.
        /// <para>
        /// Reliable, and deliberately not batched into one giant message. Over a megabyte
        /// arrives visibly slowly, and per-packet messages let the receiver report progress
        /// and distinguish "still arriving" from "the host stopped".
        /// </para>
        /// </summary>
        private void SendWorldSnapshot(RemotePeer peer)
        {
            var entries = WorldSnapshot.Build(Log);
            if (entries.Count == 0)
            {
                Log("Snapshot: nothing to send. Is a save loaded?");
                return;
            }

            var total = 0;
            foreach (var e in entries) total += e.Payload.Length;

            _session.Send(peer.Id, new WorldSnapshotV1
            {
                PacketCount = entries.Count,
                TotalBytes = total,
                TotalGameSeconds = ReadGameSeconds(),
                GameplayGameDay = ReadGameDay(),
            }.Serialise());

            foreach (var e in entries)
            {
                _session.Send(peer.Id, new ManagerPacketV1
                {
                    PacketGuid = e.PacketGuid,
                    ManagerTypeName = e.ManagerTypeName,
                    Payload = e.Payload,
                }.Serialise());
            }

            Log($"Snapshot: sent {entries.Count} packet(s), {total:N0} bytes to {peer.Name}");
        }

        // ---------------------------------------------------------------- snapshot request

        private bool _worldRequested;
        private float _worldReadyCheckTimer;
        private bool _loggedWorldReady;

        /// <summary>
        /// True once this machine is standing in a world rather than on a menu or part way
        /// through a load. A player object exists only then, and the lookup behind it is
        /// cached and throttled, so this is cheap to ask every frame.
        /// </summary>
        private bool WorldIsLoaded
        {
            get
            {
                var loaded = _transforms.LocalPlayerObject != null;

                if (loaded && !_loggedWorldReady)
                {
                    _loggedWorldReady = true;
                    Log("World loaded, replication is live");
                    _zones.DescribeScene("world loaded");
                }

                return loaded;
            }
        }

        /// <summary>
        /// Ask the host for the world once this client actually has one to apply it to.
        /// <para>
        /// Polled rather than event driven because there is no reliable "world loaded"
        /// hook: the constructor patch that would have provided one does not fire under
        /// IL2CPP. The presence of managers is the thing that actually matters, so that is
        /// what is checked.
        /// </para>
        /// </summary>
        private void MaybeRequestWorld(float deltaTime)
        {
            if (_worldRequested) return;
            if (_session == null || !_session.IsActive || _session.IsHost) return;

            // Reached only once a world exists, so the sweep below runs when there is
            // something to find rather than repeatedly through a load.
            _worldReadyCheckTimer -= deltaTime;
            if (_worldReadyCheckTimer > 0f) return;
            _worldReadyCheckTimer = 1f;

            int count;
            try { count = SaveSerialiser.FindManagers().Count; }
            catch (Exception) { return; }

            // A menu has a handful of managers at most; a loaded world has dozens.
            if (count < 10) return;

            _worldRequested = true;
            Log($"World: {count} manager(s) ready, asking the host for its world");

            // Ask for the host's SAVE, not a reconstruction of it. See
            // docs/joining-a-session.md.
            _session.SendToHost(new SaveRequestV1
            {
                PlayerName = EffectivePlayerName,
            }.Serialise());
        }

        // ---------------------------------------------------------------- save transfer

        /// <summary>A guest is ready and wants the world. Save, and send the file.</summary>
        private void OnSaveRequested(PeerId from, SaveRequestV1 request)
        {
            var who = string.IsNullOrEmpty(request.PlayerName) ? from.ToShortString() : request.PlayerName;
            Log($"Save: {who} is ready, saving so they can load this world");

            SaveTransferV1 transfer;
            try { transfer = SaveTransfer.TryCapture(Log); }
            catch (Exception ex)
            {
                LogError($"Save: capture threw: {ex}");
                return;
            }

            if (transfer == null)
            {
                LogError($"Save: nothing to send {who}. They stay in their own world.");
                return;
            }

            try
            {
                _session.Send(from, transfer.Serialise());
                Log($"Save: sent {transfer.Data.Length:N0} bytes to {who}");
            }
            catch (Exception ex)
            {
                LogError($"Save: sending failed: {ex.Message}");
            }
        }

        /// <summary>The host's world arrived. Write it down and load it.</summary>
        private void OnSaveReceived(SaveTransferV1 msg)
        {
            Log($"Save: received {msg.Data?.Length ?? 0:N0} bytes from the host");

            // Loading replaces the world, so everything tracking the old one goes first.
            // An avatar or a cached manager surviving a load would point at destroyed
            // objects.
            try
            {
                _avatars.Clear();
                _transforms.Reset();
                _clock.Reset();
                _zones.Reset();
                _loggedWorldReady = false;

                // Report where this load actually puts us, once it has finished.
                _arrivalPending = true;
                _arrivalSettle = 0f;
            }
            catch (Exception) { }

            try
            {
                if (!SaveTransfer.TryApply(msg, Log))
                    LogError("Save: could not load the host's world. Staying put.");
            }
            catch (Exception ex)
            {
                LogError($"Save: applying threw: {ex}");
            }
        }

        // ---------------------------------------------------------------- snapshot receipt

        private WorldSnapshotV1 _incomingSnapshot;
        private readonly List<ManagerPacketV1> _incomingPackets = new List<ManagerPacketV1>();

        private void BeginReceivingSnapshot(WorldSnapshotV1 header)
        {
            _incomingSnapshot = header;
            _incomingPackets.Clear();

            Log($"Snapshot: incoming, {header.PacketCount} packet(s), " +
                $"{header.TotalBytes:N0} bytes, host is on day {header.GameplayGameDay}");
        }

        private void ReceiveSnapshotPacket(ManagerPacketV1 packet)
        {
            if (_incomingSnapshot == null)
            {
                Log($"Snapshot: ignoring {packet.ManagerTypeName}, no header arrived first");
                return;
            }

            _incomingPackets.Add(packet);

            if (_incomingPackets.Count < _incomingSnapshot.PacketCount) return;

            var received = 0;
            foreach (var p in _incomingPackets) received += p.Payload?.Length ?? 0;

            Log($"Snapshot: complete, {_incomingPackets.Count} packet(s), {received:N0} bytes");
            foreach (var p in _incomingPackets)
                Log($"    {p.ManagerTypeName,-46} {p.Payload?.Length ?? 0,9:N0} b");

            if (!Config.ApplyWorldSnapshot.Value)
            {
                Log("Snapshot: received but NOT applied (ApplyWorldSnapshot is off). " +
                    "Players and the clock still replicate; the world does not.");
            }
            else
            {
                Log("Snapshot: applying. This overwrites the local world.");
                try { WorldSnapshotApplier.Apply(_incomingPackets, Log); }
                catch (Exception ex) { LogError($"Apply threw: {ex}"); }
            }

            _incomingSnapshot = null;
            _incomingPackets.Clear();
        }

        // ---------------------------------------------------------------- sleep prompt

        private readonly UI.SleepPrompt _sleepPrompt = new UI.SleepPrompt();
        private readonly List<string> _atBed = new List<string>();
        private readonly List<string> _notAtBed = new List<string>();

        /// <summary>
        /// Show the waiting panel while this player is in bed and others are not.
        /// <para>
        /// Built lazily rather than at load, because TextMeshPro and the game's font assets
        /// are not ready when the plugin starts.
        /// </para>
        /// </summary>
        private void UpdateSleepPrompt()
        {
            if (!_sleep.LocalIsWaiting)
            {
                _sleepPrompt.Hide();
                return;
            }

            if (!_sleepPrompt.Available) _sleepPrompt.Build();
            if (!_sleepPrompt.Available) return;

            _sleep.DescribeWaiting(_session, _atBed, _notAtBed);

            // Nobody left to wait for means the sleep is already under way.
            if (_notAtBed.Count == 0) { _sleepPrompt.Hide(); return; }

            _sleepPrompt.Show(_atBed, _notAtBed);
        }

        // ---------------------------------------------------------------- arrival report

        private bool _arrivalPending;
        private float _arrivalSettle;

        /// <summary>
        /// A load has begun, from anywhere: the main menu, zone travel, or a guest adopting
        /// the host's world.
        /// <para>
        /// Reported for every load rather than only for a join, because the interesting
        /// question is whether the two differ. A normal menu load of the same slot is the
        /// control: if it arrives somewhere the join does not, the join is at fault, and if
        /// both land in the same wrong place then the save itself does not round-trip the
        /// player's position.
        /// </para>
        /// </summary>
        public void OnLoadStarted()
        {
            _arrivalPending = true;
            _arrivalSettle = 0f;
        }

        /// <summary>Seconds to let the world settle after the loading screen drops.</summary>
        private const float ArrivalSettleSeconds = 1.5f;

        /// <summary>
        /// Say where a guest actually came up, once, after loading the host's world.
        /// <para>
        /// <b>Deliberately not driven off <c>WorldIsLoaded</c>.</b> That reports true around
        /// fifty milliseconds into a load, because the world being replaced still satisfies
        /// it, so anything logged there describes the OLD position and is worse than silence.
        /// The honest signal is the loading screen being down, plus a moment for the player
        /// to be placed.
        /// </para>
        /// </summary>
        private void ReportArrivalIfPending(float deltaTime)
        {
            if (!_arrivalPending) return;

            bool screenUp;
            try { screenUp = Nivalis.LoadingScreenUI.showing; }
            catch (Exception) { screenUp = false; }

            if (screenUp || _transforms.LocalPlayerObject == null)
            {
                _arrivalSettle = 0f;
                return;
            }

            _arrivalSettle += deltaTime;
            if (_arrivalSettle < ArrivalSettleSeconds) return;

            _arrivalPending = false;
            Log($"Load: arrived at {Replication.SaveTransfer.DescribeLocation()}");

            // The load is visibly over, so any pause still held for it is stale. Without
            // this a guest lands in a city where the clock never moves again.
            Replication.LoadPauseGuard.ReleaseIfStuck(Log);
        }

        // ---------------------------------------------------------------- paused notice

        private readonly UI.PausedNotice _pausedNotice = new UI.PausedNotice();

        /// <summary>
        /// Tell a guest the host has paused, rather than leaving them in a world that has
        /// silently stopped and looks hung.
        /// <para>
        /// Built lazily on first need, like the sleep prompt, because TextMeshPro and the
        /// game's font assets are not ready when the plugin loads. A player who never sees a
        /// host pause never pays for the panel.
        /// </para>
        /// </summary>
        private void UpdatePausedNotice()
        {
            var paused = _clock.IsMirroringHostPause;

            if (!paused)
            {
                if (_pausedNotice.Available) _pausedNotice.SetVisible(false);
                return;
            }

            if (!_pausedNotice.Available) _pausedNotice.Build();
            if (!_pausedNotice.Available) return;

            _pausedNotice.SetVisible(true);
        }

        // ---------------------------------------------------------------- sleep

        /// <summary>
        /// The local player walked up to a bed. True lets them lie down now, false holds
        /// them until everybody else is at a bed too.
        /// Called from the Harmony prefix on SleepManager.TryToSleep.
        /// </summary>
        public bool OnLocalSleepAttempt(Nivalis.IBed bed)
        {
            if (_session == null || !_session.IsActive) return true;

            // The wake time is the game's own business; it works it out inside the sleep
            // sequence. We only need something to agree on, so use the current clock.
            var maySleep = _sleep.RequestSleep(_session, bed, ReadGameSeconds());

            // On the host, "may sleep" means everybody else was already waiting, so this
            // interaction is the last one. Release the others too.
            if (maySleep && _session.IsHost && _session.Peers.Count > 0)
                BroadcastProceed();

            return maySleep;
        }

        /// <summary>
        /// Everybody is at a bed. Tell the clients to lie down, and do it here as well.
        /// <para>
        /// Each player runs their own sleep sequence rather than the host running one and
        /// reporting the outcome, because the sequence is the animation, the day summary,
        /// the shop rankings and the newspaper. Those are the point of sleeping; nobody
        /// should miss them because they got into bed first.
        /// </para>
        /// </summary>
        private void BroadcastProceed()
        {
            _session.Broadcast(new SleepProceedV1
            {
                TargetGameSeconds = _sleep.AgreedTarget,
            }.Serialise());

            Log("Sleep: everybody is at a bed, laying down");
        }

        /// <summary>Replay this player's own bed interaction, now that it is allowed.</summary>
        public void PerformLocalSleep()
        {
            var bed = _sleep.LocalBed;
            if (bed == null)
            {
                LogWarning("Sleep: told to lie down but no bed was remembered.");
                return;
            }

            var manager = UnityEngine.Object.FindObjectOfType<Nivalis.SleepManager>();
            if (manager == null)
            {
                LogError("Sleep: no SleepManager to replay the interaction on.");
                return;
            }

            try
            {
                SleepCoordinator.Performing = true;
                manager.TryToSleep(bed);
            }
            catch (Exception ex)
            {
                LogError($"Sleep: replaying the bed interaction threw: {ex.Message}");
            }
            finally
            {
                SleepCoordinator.Performing = false;
            }
        }

        /// <summary>Announce next frame, once the game has finished moving time.</summary>
        private void ScheduleDayAnnouncement() => _announceDayPending = true;

        private bool _announceDayPending;

        private void AnnounceDay()
        {
            _announceDayPending = false;
            _sleep.AnnounceDayAdvanced(_session, ReadGameSeconds(), ReadGameDay());
            Log($"Sleep: day advanced to {ReadGameDay()} at {ReadGameSeconds()}s");
        }

        private void OnSessionEstablished()
        {
            // NOT suppressed here.
            //
            // A session is established from the MAIN MENU, before the client has loaded
            // anything. Freezing the clock at that moment freezes it for the whole of the
            // load, and the loading sequence never completes: observed as a guest stuck on
            // the loading screen indefinitely while its world had in fact loaded fine, 57
            // managers and all, with no error anywhere.
            //
            // Suppression begins once the world is up. See the WorldIsLoaded gate in Tick.
            Log("Session established");
        }

        private void OnSessionFailed(string why)
        {
            LogError($"Session failed: {why}");
        }

        private void OnMessageReceived(PeerId from, NetReader reader)
        {
            // NOTHING TOUCHES THE GAME UNTIL IT HAS A WORLD.
            //
            // Pump runs before the WorldIsLoaded gate in Tick, by design: the connection
            // must survive a load. But that meant inbound messages were still acted on
            // while the game was loading. A guest spends a quarter of a minute connected
            // and mid-load, and in that window clock corrections were calling AddTime on a
            // half-built world and transforms were being tracked for a player that did not
            // exist.
            //
            // The handshake is handled inside the session itself, so dropping these costs
            // nothing: a clock or position message is superseded within a second anyway,
            // and the snapshot is requested by this side only once the world is up.
            if (!WorldIsLoaded)
            {
                if (reader.Type != MessageType.WorldSnapshotV1 &&
                    reader.Type != MessageType.ManagerPacketV1)
                {
                    return;
                }
            }

            switch (reader.Type)
            {
                case MessageType.ClockSyncV1:
                    // Only ever accept a clock from the host.
                    if (_session != null && !_session.IsHost)
                        _clock.Apply(ClockSyncV1.Parse(reader));
                    break;

                case MessageType.PlayerTransformV1:
                    if (_session != null)
                        _transforms.Apply(_session, from, PlayerTransformV1.Parse(reader));
                    break;

                case MessageType.WorldSnapshotRequestV1:
                {
                    if (_session == null || !_session.IsHost) break;

                    var request = WorldSnapshotRequestV1.Parse(reader);
                    Log($"World: {from.ToShortString()} is ready ({request.LocalManagerCount} manager(s)), sending");

                    foreach (var p in _session.Peers)
                    {
                        if (p.Id != from) continue;
                        try { SendWorldSnapshot(p); }
                        catch (Exception ex) { LogError($"Could not send the world snapshot: {ex}"); }
                        break;
                    }
                    break;
                }

                case MessageType.PlayerZoneV1:
                    if (_session != null && !_session.IsHost)
                        _zones.OnHostZone(PlayerZoneV1.Parse(reader));
                    break;

                case MessageType.SleepRequestV1:
                    if (_session != null && _session.IsHost)
                    {
                        _sleep.OnRequest(_session, from, SleepRequestV1.Parse(reader));

                        if (_sleep.LastState != null &&
                            _sleep.LastState.ReadyCount >= _sleep.LastState.TotalCount)
                        {
                            BroadcastProceed();
                            PerformLocalSleep();   // the host lies down too
                        }
                    }
                    break;

                case MessageType.SleepProceedV1:
                    if (_session != null && !_session.IsHost)
                    {
                        SleepProceedV1.Parse(reader);
                        _sleep.OnProceed();
                        PerformLocalSleep();
                    }
                    break;

                case MessageType.SleepStateV1:
                    if (_session != null && !_session.IsHost)
                        _sleep.OnState(SleepStateV1.Parse(reader));
                    break;

                case MessageType.DayAdvancedV1:
                    if (_session != null && !_session.IsHost)
                    {
                        var advanced = DayAdvancedV1.Parse(reader);
                        _sleep.OnDayAdvanced();
                        _clock.ApplyDayAdvance(advanced);
                    }
                    break;

                case MessageType.SaveRequestV1:
                    if (_session != null && _session.IsHost)
                        OnSaveRequested(from, SaveRequestV1.Parse(reader));
                    break;

                case MessageType.SaveTransferV1:
                    if (_session != null && !_session.IsHost)
                        OnSaveReceived(SaveTransferV1.Parse(reader));
                    break;

                case MessageType.WorldSnapshotV1:
                    if (_session != null && !_session.IsHost)
                        BeginReceivingSnapshot(WorldSnapshotV1.Parse(reader));
                    break;

                case MessageType.ManagerPacketV1:
                    if (_session != null && !_session.IsHost)
                        ReceiveSnapshotPacket(ManagerPacketV1.Parse(reader));
                    break;
            }
        }

        // ---------------------------------------------------------------- diagnostics

        private void BeginRecording(bool isHost)
        {
            if (!EffectiveRecordTraces) return;

            try
            {
                var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{(isHost ? "host" : "client")}.ntrace";
                var path = Path.Combine(NightsharePlugin.ArtifactsDir, name);

                _recorder = PacketRecorder.ToFile(path, NightsharePlugin.Version,
                                                  GameFingerprint.Current, isHost);
                Log($"Recording trace to {path}");
            }
            catch (Exception ex)
            {
                LogWarning($"Could not start a trace: {ex.Message}");
                _recorder = null;
            }
        }

        /// <summary>
        /// Start the player rig probe. Turn the character while it runs, and it reports
        /// which transform actually carries the facing.
        /// </summary>
        public void ProbePlayerRig() => _rigProbe.Begin(_transforms.LocalPlayerObject);

        /// <summary>
        /// Prove the save serialiser round-trips every manager, without applying anything.
        /// The safe first step before a world snapshot touches a live world.
        /// </summary>
        public void RunSaveSelfTest()
        {
            try { SaveSerialiser.RunSelfTest(Log); }
            catch (Exception ex) { LogError($"Save self test threw: {ex}"); }
        }

        public void LogStatus()
        {
            if (_session == null) { Log("No session."); return; }

            Log("--- Nightshare status ---");
            Log($"  state      : {_session.State}");
            Log($"  role       : {_session.Role}");
            Log($"  local peer : {_session.LocalPeer.ToShortString()}");
            Log($"  session id : {_session.SessionId}");
            Log($"  game build : {GameFingerprint.Current}");
            Log($"  clock      : day {ReadGameDay()} at {ReadGameSeconds()}s" +
                $"{(_session != null && !_session.IsHost ? " (following host)" : "")}");
            Log($"  clock sent : {_clock.BroadcastCount}");
            Log($"  clock recv : {_clock.AppliedCount}");
            Log($"  pos sent   : {_transforms.SentCount}");
            Log($"  pos recv   : {_transforms.ReceivedCount}");

            foreach (var kv in _transforms.Remotes)
            {
                var r = kv.Value;
                Log($"    {(string.IsNullOrEmpty(r.Name) ? r.Peer.ToShortString() : r.Name)} " +
                    $"at ({r.Position.x:0.0}, {r.Position.y:0.0}, {r.Position.z:0.0}) " +
                    $"yaw {r.Yaw:0}{(r.IsMoving ? " moving" : "")}  x{r.UpdateCount}");
            }

            _avatars.Describe(Log);

            if (_session.Peers.Count == 0)
            {
                Log("  peers      : none");
            }
            else
            {
                Log($"  peers      : {_session.Peers.Count}");
                foreach (var p in _session.Peers) Log($"    {p}");
            }

            if (_session.FailureReason != null)
                Log($"  last error : {_session.FailureReason}");
        }

        // ---------------------------------------------------------------- game clock

        private static int ReadGameSeconds()
        {
            try { return Nivalis.TimeOfDayManager.TotalGameSeconds; }
            catch { return 0; }
        }

        private static int ReadGameDay()
        {
            try { return Nivalis.TimeOfDayManager.GameplayGameDay; }
            catch { return 0; }
        }

        // ---------------------------------------------------------------- logging

        // Everything goes to both sinks. BepInEx's log is convenient when one instance is
        // running; the per-process file is the only one that works when two are.
        private static void Log(string m)
        {
            NightsharePlugin.Logger?.LogInfo(m);
            NightshareLog.Write("INFO", m);
        }

        private static void LogWarning(string m)
        {
            NightsharePlugin.Logger?.LogWarning(m);
            NightshareLog.Write("WARNING", m);
        }

        private static void LogError(string m)
        {
            NightsharePlugin.Logger?.LogError(m);
            NightshareLog.Write("ERROR", m);
        }
    }
}
