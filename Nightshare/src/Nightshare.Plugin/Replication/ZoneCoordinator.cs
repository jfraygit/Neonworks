using System;
using Nightshare.Core.Protocol;
using Nightshare.Core.Session;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nightshare.Replication
{
    /// <summary>
    /// Keeps a joining guest in a zone that actually exists around them.
    /// <para>
    /// Nivalis is 53 separate scenes, not one world. Only the district a player is in is
    /// loaded. A guest whose save puts them somewhere the host is not therefore arrives
    /// inside a box of unloaded geometry with no way out, which is exactly what happened in
    /// testing.
    /// </para>
    /// <para>
    /// On joining, the guest travels to wherever the host is. That is also simply the right
    /// behaviour: you joined someone, so you should arrive where they are.
    /// </para>
    /// <para>
    /// <b>Once, on joining, and never again.</b> After arrival the two move independently.
    /// Each instance loads its own district, so two players in different zones is a normal
    /// state rather than a fault, and dragging a guest along every time the host took a
    /// taxi would teleport them out of whatever they were doing.
    /// </para>
    /// </summary>
    internal sealed class ZoneCoordinator
    {
        private string _lastAnnounced;
        private string _hostScene;
        private bool _travelAttempted;
        private bool _warnedCannotTravel;
        private float _retryTimer;

        public string HostScene => _hostScene;

        public void Reset()
        {
            _lastAnnounced = null;
            _hostScene = null;
            _travelAttempted = false;
            _retryTimer = 0f;
        }

        /// <summary>The scene this player is standing in.</summary>
        public static string CurrentScene()
        {
            try { return SceneManager.GetActiveScene().name; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Report what the active scene actually is.
        /// <para>
        /// The zone logic produced no log output at all in a session where it should have,
        /// which means the scene name is empty or the lookup fails. Guessing which cost a
        /// test run; this says so outright.
        /// </para>
        /// </summary>
        public void DescribeScene(string context)
        {
            string scene;
            try { scene = SceneManager.GetActiveScene().name; }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Zone [{context}]: scene lookup threw: {ex.Message}");
                return;
            }

            NightsharePlugin.Logger?.LogInfo(
                $"Zone [{context}]: active scene is " +
                $"{(string.IsNullOrEmpty(scene) ? "EMPTY, zone sync cannot work" : $"'{scene}'")}");
        }

        // ---------------------------------------------------------------- host

        /// <summary>
        /// Tell everybody which zone the host is in, whenever it changes. Cheap enough to
        /// check every frame and it only sends on a change.
        /// </summary>
        public void HostTick(NightshareSession session)
        {
            if (session == null || !session.IsActive || !session.IsHost) return;
            if (session.Peers.Count == 0) return;

            var scene = CurrentScene();
            if (string.IsNullOrEmpty(scene) || scene == _lastAnnounced) return;

            _lastAnnounced = scene;

            session.Broadcast(new PlayerZoneV1
            {
                Peer = session.LocalPeer,
                SceneName = scene,
            }.Serialise());

            NightsharePlugin.Logger?.LogInfo($"Zone: host is in '{scene}', told the guests");
        }

        // ---------------------------------------------------------------- client

        public void OnHostZone(PlayerZoneV1 msg)
        {
            if (string.IsNullOrEmpty(msg.SceneName)) return;

            if (_hostScene == msg.SceneName) return;

            _hostScene = msg.SceneName;
            NightsharePlugin.Logger?.LogInfo($"Zone: host is in '{_hostScene}'");

            // DELIBERATELY DOES NOT RESET _travelAttempted.
            //
            // A guest follows the host exactly once, on joining. After that the two move
            // independently: each instance loads its own district, so being in different
            // zones is a normal state rather than a fault. Following every time the host
            // took a taxi would teleport a guest out of whatever they were in the middle
            // of doing, which is worse than being apart.
        }

        /// <summary>
        /// Travel to the host's zone if we are not already in it. Called every frame on a
        /// client; does nothing once we have arrived.
        /// </summary>
        public void ClientTick(NightshareSession session, bool worldIsLoaded, float deltaTime)
        {
            if (session == null || !session.IsActive || session.IsHost) return;
            if (string.IsNullOrEmpty(_hostScene) || _travelAttempted) return;

            // NEVER TRAVEL WITHOUT A WORLD.
            //
            // A client auto-joins from the MAIN MENU, long before it loads a save. The
            // menu's scene name obviously differs from the host's, so without this guard
            // the guest immediately asks to travel while no world exists. That produced an
            // unbroken flood of NullReferenceExceptions and a client with no menu at all,
            // which is a far worse outcome than being in the wrong district.
            if (!worldIsLoaded) return;

            var scene = CurrentScene();
            if (string.IsNullOrEmpty(scene)) return;

            if (scene == _hostScene)
            {
                _travelAttempted = true;       // already together, nothing to do
                return;
            }

            // Travel is refused during a load or a cutscene, so keep asking rather than
            // giving up on the first no.
            _retryTimer -= deltaTime;
            if (_retryTimer > 0f) return;
            _retryTimer = 2f;

            TravelToHost();
        }

        private void TravelToHost()
        {
            Nivalis.TravelManager manager;
            try
            {
                manager = UnityEngine.Object.FindObjectOfType<Nivalis.TravelManager>();
            }
            catch (Exception) { return; }

            if (manager == null) return;

            try
            {
                if (!manager.CanTravel)
                {
                    // Once only. This is polled every two seconds and a load or a cutscene
                    // can hold it off for a while, so logging each attempt fills the file.
                    if (!_warnedCannotTravel)
                    {
                        _warnedCannotTravel = true;
                        NightsharePlugin.Logger?.LogInfo("Zone: cannot travel yet, will keep trying");
                    }
                    return;
                }
            }
            catch (Exception) { }

            var key = FindPortalForScene(_hostScene);
            if (key == null)
            {
                NightsharePlugin.Logger?.LogWarning(
                    $"Zone: no portal found leading to '{_hostScene}'. " +
                    $"The guest is stranded in '{CurrentScene()}'.");
                _travelAttempted = true;       // nothing more to try
                return;
            }

            try
            {
                NightsharePlugin.Logger?.LogInfo($"Zone: travelling to the host in '{_hostScene}'");

                // forceReload: true. The point is to get the district actually loaded, and
                // a travel the game thinks is unnecessary would leave us where we are.
                manager.RequestTravel(key, isCabTravel: false, forceReload: true);
                _travelAttempted = true;
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Zone: travel failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Find a portal whose destination is the given scene.
        /// <para>
        /// Every PortalKey knows its own SceneName, so the whole set can be searched
        /// without needing the location graph or a map of the city.
        /// </para>
        /// </summary>
        private static Nivalis.PortalKey FindPortalForScene(string sceneName)
        {
            try
            {
                var keys = Resources.FindObjectsOfTypeAll<Nivalis.PortalKey>();
                if (keys == null) return null;

                foreach (var key in keys)
                {
                    if (key == null) continue;

                    string target;
                    try { target = key.SceneName; }
                    catch (Exception) { continue; }

                    if (string.Equals(target, sceneName, StringComparison.Ordinal)) return key;
                }
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Zone: portal search failed: {ex.Message}");
            }

            return null;
        }
    }
}
