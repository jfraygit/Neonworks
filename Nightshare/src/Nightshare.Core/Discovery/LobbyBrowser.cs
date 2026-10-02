using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Nightshare.Core.Discovery
{
    /// <summary>One session a guest can see, as the menu lists it.</summary>
    public sealed class FoundLobby
    {
        public string HostName = "";
        public string Address = "";
        public int Port;
        public int PlayerCount;
        public string SessionId = "";

        /// <summary>False when the builds or protocol differ. Listed, but not joinable.</summary>
        public bool Compatible = true;

        /// <summary>Why it cannot be joined, for the menu to show.</summary>
        public string Incompatibility = "";

        /// <summary>Seconds since the last beacon, used to drop sessions that went away.</summary>
        public float Age;

        public string Endpoint => $"{Address}:{Port}";
    }

    /// <summary>
    /// Listens for hosts announcing themselves, and keeps a list of what is out there.
    /// <para>
    /// <b>A session that stops being announced disappears.</b> Entries are aged out rather
    /// than removed on any kind of goodbye, because a host that crashes or has its network
    /// pulled never sends one, and a list that keeps offering a session nobody can join is
    /// worse than a list that is a second out of date.
    /// </para>
    /// </summary>
    public sealed class LobbyBrowser : IDisposable
    {
        /// <summary>Drop a session this long after its last beacon.</summary>
        public float ForgetAfterSeconds { get; set; } = 4f;

        private UdpClient _socket;
        private readonly Dictionary<string, FoundLobby> _found = new Dictionary<string, FoundLobby>();
        private readonly List<string> _expired = new List<string>();

        /// <summary>What this peer is, so incompatible sessions can be marked as such.</summary>
        public int ExpectedProtocol { get; set; }
        public string ExpectedModVersion { get; set; } = "";
        public string ExpectedGameBuild { get; set; } = "";

        public Action<string> Log { get; set; }

        public bool IsRunning => _socket != null;

        public void Start()
        {
            if (_socket != null) return;

            try
            {
                _socket = new UdpClient();

                // TWO COPIES ON ONE MACHINE BOTH NEED THIS PORT.
                //
                // Without address reuse the second instance fails to bind, and the second
                // instance is the guest, which is the one that needs to listen. That is the
                // whole local test setup.
                _socket.Client.SetSocketOption(SocketOptionLevel.Socket,
                                               SocketOptionName.ReuseAddress, true);
                _socket.Client.Bind(new IPEndPoint(IPAddress.Any, LobbyBeacon.DiscoveryPort));
                _socket.Client.Blocking = false;
            }
            catch (Exception ex)
            {
                _socket = null;
                Log?.Invoke($"Lobby: cannot listen for sessions ({ex.Message}). " +
                            $"You can still join by typing an address.");
            }
        }

        public void Stop()
        {
            try { _socket?.Close(); } catch (Exception) { }
            _socket = null;
            _found.Clear();
        }

        /// <summary>Call every frame while the browser is open.</summary>
        public void Tick(float deltaTime)
        {
            Drain();
            Age(deltaTime);
        }

        private void Drain()
        {
            if (_socket == null) return;

            // Bounded, so a flood cannot hold the frame. Anything not read this frame is
            // still queued and a beacon repeats every second anyway.
            for (var i = 0; i < 16; i++)
            {
                byte[] data;
                IPEndPoint sender = null;

                try
                {
                    if (_socket.Available <= 0) return;
                    data = _socket.Receive(ref sender);
                }
                catch (SocketException)
                {
                    return;      // nothing waiting, or the socket went away
                }
                catch (Exception)
                {
                    return;
                }

                if (data == null || sender == null) continue;
                Accept(data, sender);
            }
        }

        /// <summary>
        /// Take one datagram as if it had arrived on the wire.
        /// <para>
        /// Public because it is the whole of this class that is worth testing, and a socket
        /// in a test is a flaky test. It also states the contract plainly: <b>anything at all
        /// may arrive here</b>. The discovery port is open to the network and most of what
        /// lands on it belongs to something else, so malformed input is ignored rather than
        /// reported.
        /// </para>
        /// </summary>
        public void Accept(byte[] data, IPEndPoint sender)
        {
            if (data == null || data.Length == 0 || sender == null) return;

            try { AcceptCore(data, sender); }
            catch (Exception)
            {
                // Not an error. Not everything on this port is ours.
            }
        }

        private void AcceptCore(byte[] data, IPEndPoint sender)
        {
            using var reader = new Protocol.NetReader(data);
            if (reader.Type != Protocol.MessageType.LobbyBeaconV1) return;

            var beacon = LobbyBeaconV1.Parse(reader);
            if (string.IsNullOrEmpty(beacon.SessionId)) return;

            if (!_found.TryGetValue(beacon.SessionId, out var lobby))
            {
                lobby = new FoundLobby { SessionId = beacon.SessionId };
                _found[beacon.SessionId] = lobby;
            }

            lobby.HostName = string.IsNullOrWhiteSpace(beacon.HostName) ? "A player" : beacon.HostName;

            // The sender's address, never one the host claimed. A host does not reliably
            // know which of its addresses a guest can reach.
            lobby.Address = sender.Address.ToString();
            lobby.Port = beacon.Port;
            lobby.PlayerCount = beacon.PlayerCount;
            lobby.Age = 0f;

            lobby.Compatible = true;
            lobby.Incompatibility = "";

            if (ExpectedProtocol != 0 && beacon.ProtocolVersion != ExpectedProtocol)
            {
                lobby.Compatible = false;
                lobby.Incompatibility = "Different Nightshare protocol";
            }
            else if (!string.IsNullOrEmpty(ExpectedModVersion) &&
                     beacon.ModVersion != ExpectedModVersion)
            {
                lobby.Compatible = false;
                lobby.Incompatibility = $"They are on Nightshare {beacon.ModVersion}";
            }
            else if (!string.IsNullOrEmpty(ExpectedGameBuild) &&
                     beacon.GameBuildId != ExpectedGameBuild)
            {
                lobby.Compatible = false;
                lobby.Incompatibility = "Different game version";
            }
        }

        private void Age(float deltaTime)
        {
            if (_found.Count == 0) return;

            _expired.Clear();

            foreach (var pair in _found)
            {
                pair.Value.Age += deltaTime;
                if (pair.Value.Age > ForgetAfterSeconds) _expired.Add(pair.Key);
            }

            foreach (var key in _expired) _found.Remove(key);
        }

        /// <summary>
        /// Everything currently visible, most recently heard from first so the list does not
        /// reorder under someone about to press Enter.
        /// </summary>
        public void Snapshot(List<FoundLobby> into)
        {
            into.Clear();
            foreach (var lobby in _found.Values) into.Add(lobby);
            into.Sort((a, b) => string.CompareOrdinal(a.SessionId, b.SessionId));
        }

        public void Dispose() => Stop();
    }
}
