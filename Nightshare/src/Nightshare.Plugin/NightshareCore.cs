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

        // ---------------------------------------------------------------- what the menu asks

        /// <summary>A session at a glance, for the Nightshare menu to describe.</summary>
        public struct Summary
        {
            public bool Active;
            public bool IsHost;
            public int PeerCount;
            public string HostName;
            public string Endpoint;

            /// <summary>Mid-handshake, so the menu can say so rather than looking idle.</summary>
            public bool Connecting;

            /// <summary>
            /// The last thing that went wrong, for the menu to show. Cleared when something
            /// is tried again, so it describes the most recent attempt and not a stale one.
            /// </summary>
            public string LastError;
        }

        private string _lastSessionError;

        public Summary SessionSummary
        {
            get
            {
                var s = new Summary
                {
                    Endpoint = EffectiveEndpoint,
                    HostName = "the host",
                    LastError = _lastSessionError,
                };

                try
                {
                    if (_session == null) return s;

                    s.Connecting = _session.State is SessionState.Connecting
                                                  or SessionState.Handshaking;

                    if (!_session.IsActive) return s;

                    s.Active = true;
                    s.IsHost = _session.IsHost;
                    s.PeerCount = _session.Peers.Count;

                    foreach (var p in _session.Peers)
                    {
                        if (!p.IsHost) continue;
                        s.HostName = p.Name;
                        break;
                    }
                }
                catch (Exception) { }

                return s;
            }
        }

        /// <summary>
        /// Whether this machine is standing in a world. The menu gates hosting and joining
        /// on it, because both need one and a session started without one is where a long
        /// run of bugs came from.
        /// </summary>
        public bool HasWorld
        {
            get
            {
                try { return _transforms.LocalPlayerObject != null; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>The address half of the endpoint, for the menu to show and edit.</summary>
        public string LobbyAddress => EndpointText.Address(EffectiveEndpoint);

        /// <summary>The port half, or the default when the endpoint is malformed.</summary>
        public int LobbyPort => EndpointText.Port(EffectiveEndpoint);

        public void SetLobbyAddress(string address) => SetEndpoint(address, LobbyPort);

        public void SetLobbyPort(int port) => SetEndpoint(LobbyAddress, port);

        /// <summary>
        /// Write the endpoint back to config so it survives a restart.
        /// <para>
        /// A command line override still wins while the process lives, since the launch
        /// scripts rely on it, but the typed value is what is there next time.
        /// </para>
        /// </summary>
        private void SetEndpoint(string address, int port)
        {
            if (string.IsNullOrWhiteSpace(address)) return;

            // REFUSE AN ADDRESS NOBODY CAN CONNECT TO.
            //
            // A half-typed "7" is a perfectly valid IPv4 address as far as the framework is
            // concerned: it parses to 0.0.0.7. Stored, it then sat in the config across
            // restarts and broke every attempt to connect, with nothing on screen to say
            // why. Rejecting it at the point it is typed is the only place the player still
            // has the context to fix it.
            if (!EndpointText.LooksConnectable(address))
            {
                _lastSessionError = $"'{address}' is not an address anyone can reach.";
                LogWarning($"Lobby: refused the address '{address}'.");
                return;
            }

            _lastSessionError = null;
            var endpoint = EndpointText.Format(address, port);

            try
            {
                Config.Endpoint.Value = endpoint;
                Log($"Lobby: endpoint set to {endpoint}");
            }
            catch (Exception ex)
            {
                LogError($"Could not save the endpoint: {ex.Message}");
            }
        }
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
            // BEFORE THE GATE, DELIBERATELY.
            //
            // Everything below needs a world. The loading-screen hold is the one thing that
            // must keep running when there is not one, because during a load the local
            // player is destroyed and this gate closes. Leaving the timeout on the far side
            // of it means a load that never finishes holds the screen up forever, which is
            // the worst bug this mod has had.
            TickLoadingScreenHold(deltaTime);

            // Also before the gate. The menu's whole job is to be reachable, including at
            // the main menu where it explains that a save has to be loaded first.
            TickDiscovery(deltaTime);

            try { _lobby.Tick(); }
            catch (Exception ex) { LogError($"Lobby menu threw: {ex.Message}"); }

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

            // These are DontDestroyOnLoad, so without this they survive the plugin.
            try { _sleepPrompt.Destroy(); } catch { }
            try { _pausedNotice.Destroy(); } catch { }
            try { _lobby.Destroy(); } catch { }

            // Sockets, so these leak rather than merely linger if they are not closed.
            try { _beacon.Dispose(); } catch { }
            try { _browser.Dispose(); } catch { }
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

        /// <summary>
        /// Turn a socket error into something worth putting on screen.
        /// <para>
        /// The framework's wording describes what the operating system refused, not what
        /// the player should do. "The requested address is not valid in its context" is
        /// accurate and useless.
        /// </para>
        /// </summary>
        private static string Readable(string why)
        {
            if (string.IsNullOrEmpty(why)) return "Something went wrong.";

            if (why.Contains("already in use") || why.Contains("only one usage"))
                return "That port is already in use. Try another.";

            if (why.Contains("not valid in its context"))
                return "That address cannot be used. Check it, or leave it as 127.0.0.1.";

            if (why.Contains("refused") || why.Contains("actively refused"))
                return "Nobody is hosting there yet.";

            if (why.Contains("timed out") || why.Contains("did not properly respond"))
                return "No answer. Check the address, or that they are hosting.";

            return why;
        }

        public void StartHosting()
        {
            if (IsBusy()) return;

            // Cleared on every attempt, so what is on screen is about this try.
            _lastSessionError = null;

            Log($"Hosting on {EffectiveEndpoint} as '{EffectivePlayerName}'");
            BeginRecording(isHost: true);
            _session.Host(CreateTransport(), EffectiveEndpoint, BuildIdentity());
        }

        public void StartJoining()
        {
            if (IsBusy()) return;

            _lastSessionError = null;

            Log($"Joining {EffectiveEndpoint} as '{EffectivePlayerName}'");
            BeginRecording(isHost: false);
            _session.Join(CreateTransport(), EffectiveEndpoint, BuildIdentity());
        }

        public void Leave()
        {
            if (_session == null) return;

            // Hand the clock back first, or a client that leaves is frozen in time forever
            // with no obvious cause.
            ResetSessionState();
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

        // THE WORLD SNAPSHOT IS GONE. DO NOT BRING IT BACK.
        //
        // A joining guest used to be sent the world as one message per manager, around
        // thirty of them, which it applied to its running game. It never worked properly.
        // Managers have a two-phase init that had to be discovered, several packets failed
        // to round-trip, the quest UI stayed stale afterwards, and NPCs ended up in states
        // the game could not have produced. Every fix revealed another system that needed
        // the same treatment, because the set of things a world is made of is not knowable
        // from outside.
        //
        // Sending the host's SAVE FILE and letting the game load it replaced all of it. The
        // game already knows how to turn that file into a world; it does it every time
        // anyone presses Continue. See docs/joining-a-session.md.

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

            SaveTransferV2 transfer;
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
        private void OnSaveReceived(SaveTransferV2 msg)
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

                // HELD UNTIL THE LOAD FINISHES, NOT APPLIED NOW.
                //
                // Loading from inside a running world does not restore the saved player
                // transform; the game places the player at the zone's arrival point. So the
                // host's position is re-applied afterwards, and it has to outlive the load
                // to do that.
                _pendingArrival = msg;
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
        private string _arrivalWhat = "a load";

        /// <summary>
        /// The transfer whose arrival point still needs applying, or null. Only a join sets
        /// this; an ordinary menu load has nowhere particular to be.
        /// </summary>
        private SaveTransferV2 _pendingArrival;

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
        public void OnLoadStarted(string saveName, bool fromTheGamesOwnMenu)
        {
            _arrivalPending = true;
            _arrivalSettle = 0f;

            // CARRIED THROUGH TO THE ARRIVAL LINE ON PURPOSE.
            //
            // A session has several loads in it: the player's own save at the start, the
            // join, zone travel, a menu load used as a control. They all produced an
            // identical "arrived at" line, so reading one back meant counting loads and
            // hoping. Saying which load this was removes the guess.
            _arrivalWhat = fromTheGamesOwnMenu
                ? $"the game's own menu load of '{saveName}'"
                : $"an in-game load of '{saveName}'";

            _gameplaySceneReady = false;

            // Only a join needs the screen held. An ordinary load has no correction coming
            // afterwards, so holding it would just make the game feel slower.
            _holdingLoadingScreen = _pendingArrival != null;
            _holdElapsed = 0f;

            Replication.LoadPauseGuard.LoadStarted();
        }

        /// <summary>Seconds to let the world settle once the gameplay scene is up.</summary>
        private const float ArrivalSettleSeconds = 1.0f;

        /// <summary>
        /// Longest the loading screen is held waiting for a guest to be placed.
        /// <para>
        /// <b>A hold must always end.</b> Holding is a cosmetic improvement; a player stuck
        /// behind a loading screen forever is the worst failure this mod has had, twice. If
        /// placement has not happened by now, something is wrong and the right answer is to
        /// show them the world anyway.
        /// </para>
        /// </summary>
        private const float MaxArrivalHoldSeconds = 12f;

        /// <summary>Set when the gameplay scene finishes loading. The real readiness signal.</summary>
        private bool _gameplaySceneReady;

        private bool _holdingLoadingScreen;
        private float _holdElapsed;

        /// <summary>True only while we are calling Hide ourselves, so the patch lets it pass.</summary>
        private bool _releasingHold;

        /// <summary>
        /// Whether the loading screen should stay up. Read by the patch on
        /// <c>LoadingScreenUI.Hide</c>.
        /// <para>
        /// A guest's arrival is a load, then a correction: the game puts them at the zone's
        /// arrival point and we move them to the host. Letting the screen drop in between
        /// shows two seconds of the wrong place and a teleport, which reads as a bug even
        /// though it is working. Holding turns it into one transition.
        /// </para>
        /// </summary>
        public bool ShouldHoldLoadingScreen() => _holdingLoadingScreen && !_releasingHold;

        /// <summary>The gameplay scene is up. Called from the scene-loaded patch.</summary>
        public void OnGameplaySceneLoaded() => _gameplaySceneReady = true;

        /// <summary>
        /// The deadline on the hold. Runs every frame whether or not there is a world,
        /// because the case it guards against is there never being one.
        /// </summary>
        private void TickLoadingScreenHold(float deltaTime)
        {
            if (!_holdingLoadingScreen) return;

            _holdElapsed += deltaTime;
            if (_holdElapsed <= MaxArrivalHoldSeconds) return;

            LogWarning($"Arrival: gave up holding the loading screen after " +
                       $"{MaxArrivalHoldSeconds:0}s. Showing the world unplaced.");
            ReleaseLoadingScreen();
        }

        /// <summary>
        /// Say where a guest actually came up, and put them beside the host.
        /// <para>
        /// <b>Driven off the gameplay scene loading, not the loading screen.</b> It used to
        /// wait for the screen to come down, which cannot work once we are the reason it is
        /// still up. <c>WorldIsLoaded</c> is no good either: it reports true about fifty
        /// milliseconds into a load, because the world being replaced still satisfies it.
        /// </para>
        /// </summary>
        private void ReportArrivalIfPending(float deltaTime)
        {
            if (!_arrivalPending) return;

            if (!_gameplaySceneReady || _transforms.LocalPlayerObject == null)
            {
                _arrivalSettle = 0f;
                return;
            }

            _arrivalSettle += deltaTime;
            if (_arrivalSettle < ArrivalSettleSeconds) return;

            _arrivalPending = false;
            Log($"Load: after {_arrivalWhat}, arrived at {Replication.SaveTransfer.DescribeLocation()}");

            // The load is visibly over, so any pause still held for it is stale. Without
            // this a guest lands in a city where the clock never moves again.
            Replication.LoadPauseGuard.ReleaseStaleLoadPauses(Log);

            // Then put a joining guest where the host actually is, since the load did not.
            if (_pendingArrival != null)
            {
                var arrival = _pendingArrival;
                _pendingArrival = null;

                try
                {
                    if (Replication.SaveTransfer.TryPlaceAtArrival(arrival, Log))
                        Log($"Load: now at {Replication.SaveTransfer.DescribeLocation()}");
                }
                catch (Exception ex) { LogError($"Arrival placement threw: {ex.Message}"); }

                // The clock is in the same position as the player was: the load did not
                // restore it, and the ordinary sync only moves forward, so a guest that
                // arrives ahead of the host never comes back on its own.
                try
                {
                    _clock.ForceTo(arrival.TotalGameSeconds, "joined the host's world",
                                   arrival.GameDay, arrival.Hour, arrival.Minute, arrival.Second);
                }
                catch (Exception ex) { LogError($"Arrival clock snap threw: {ex.Message}"); }
            }

            // Everything the player would have seen go wrong has now happened. Show them.
            ReleaseLoadingScreen();
        }

        /// <summary>
        /// Let the loading screen come down, hiding it ourselves since the game's own call
        /// was refused while we were holding.
        /// </summary>
        private void ReleaseLoadingScreen()
        {
            if (!_holdingLoadingScreen) return;

            _holdingLoadingScreen = false;
            _holdElapsed = 0f;

            try
            {
                // The flag is what lets our own Hide through the patch that blocks the
                // game's. Cleared in a finally so a throw cannot wedge the screen up.
                _releasingHold = true;
                Nivalis.LoadingScreenUI.Instance?.Hide();
                Log("Arrival: loading screen released");
            }
            catch (Exception ex)
            {
                LogError($"Arrival: could not hide the loading screen: {ex.Message}");
            }
            finally
            {
                _releasingHold = false;
            }
        }

        // ---------------------------------------------------------------- paused notice

        private readonly UI.PausedNotice _pausedNotice = new UI.PausedNotice();

        /// <summary>The Nightshare menu. Opening it is how a session is started or joined.</summary>
        private readonly UI.LobbyController _lobby = new UI.LobbyController();

        // ---------------------------------------------------------------- discovery

        private readonly Core.Discovery.LobbyBeacon _beacon = new Core.Discovery.LobbyBeacon();
        private readonly Core.Discovery.LobbyBrowser _browser = new Core.Discovery.LobbyBrowser();

        /// <summary>Begin listening for sessions on the local network.</summary>
        public void StartLookingForSessions()
        {
            try
            {
                _browser.Log = LogWarning;
                _browser.ExpectedProtocol = ProtocolVersion.Current;
                _browser.ExpectedModVersion = NightsharePlugin.Version;
                _browser.ExpectedGameBuild = GameFingerprint.Current;
                _browser.Start();
            }
            catch (Exception ex) { LogError($"Could not look for sessions: {ex.Message}"); }
        }

        public void StopLookingForSessions()
        {
            try { _browser.Stop(); } catch (Exception) { }
        }

        public void CollectFoundSessions(List<Core.Discovery.FoundLobby> into)
        {
            try { _browser.Snapshot(into); }
            catch (Exception) { into.Clear(); }
        }

        /// <summary>
        /// Keep the beacon and the browser turned over. Driven from the tick ahead of the
        /// world gate, because finding a session is something you do before joining one and
        /// the browser is useless if it only runs once you already have.
        /// </summary>
        private void TickDiscovery(float deltaTime)
        {
            try
            {
                var hosting = _session != null && _session.IsActive && _session.IsHost;

                if (hosting && !_beacon.IsRunning)
                {
                    _beacon.Log = LogWarning;
                    _beacon.Describe = DescribeSessionForBeacon;
                    _beacon.Start();
                }
                else if (!hosting && _beacon.IsRunning)
                {
                    _beacon.Stop();
                }

                _beacon.Tick(deltaTime);
                _browser.Tick(deltaTime);
            }
            catch (Exception ex) { LogError($"Discovery tick threw: {ex.Message}"); }
        }

        private Core.Discovery.LobbyBeaconV1 DescribeSessionForBeacon()
        {
            if (_session == null || !_session.IsActive || !_session.IsHost) return null;

            return new Core.Discovery.LobbyBeaconV1
            {
                HostName = EffectivePlayerName,
                Port = EndpointText.Port(EffectiveEndpoint),
                ProtocolVersion = ProtocolVersion.Current,
                ModVersion = NightsharePlugin.Version,
                GameBuildId = GameFingerprint.Current,
                PlayerCount = _session.Peers.Count,
                SessionId = _session.SessionId ?? "",
            };
        }

        /// <summary>Open or close the menu. Bound to a hotkey by the runner.</summary>
        public void ToggleLobbyMenu() => _lobby.Toggle();

        /// <summary>
        /// True while the menu has the keyboard, so the runner's other hotkeys stand down.
        /// Typing a port would otherwise also be firing join and leave.
        /// </summary>
        public bool LobbyCapturesInput => _lobby.CapturesInput;

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
            _lastSessionError = Readable(why);

            // A SESSION CAN END WITHOUT ANYONE PRESSING LEAVE.
            //
            // The per-session state used to be cleared only in Leave(). A host that
            // disconnects never goes through it, so _worldRequested stayed true, and the
            // next join connected, said nothing, and never asked for the host's world. The
            // guest sat in a stale copy of it looking like the transfer had worked.
            ResetSessionState();
        }

        /// <summary>
        /// Everything that must not outlive a session, however it ended.
        /// </summary>
        private void ResetSessionState()
        {
            // A session that ends mid-join must not leave the screen held for its timeout.
            try { ReleaseLoadingScreen(); } catch (Exception) { }
            _pendingArrival = null;

            try { _clock.Reset(); } catch (Exception) { }
            try { Replication.LoadPauseGuard.Forget(); } catch (Exception) { }
            try { _avatars.Clear(); } catch (Exception) { }
            try { _transforms.Reset(); } catch (Exception) { }
            try { _zones.Reset(); } catch (Exception) { }

            _worldRequested = false;
            _worldReadyCheckTimer = 0f;
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
            // and the world is asked for by this side only once it has one.
            //
            // The snapshot messages used to be exempt here, because they were the one thing
            // a client needed while its world was still coming up. Nothing is exempt now.
            if (!WorldIsLoaded) return;

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

                case MessageType.SaveTransferV2:
                    if (_session != null && !_session.IsHost)
                        OnSaveReceived(SaveTransferV2.Parse(reader));
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
