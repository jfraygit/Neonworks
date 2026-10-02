using System;
using System.Net;
using System.Net.Sockets;

namespace Nightshare.Core.Discovery
{
    /// <summary>
    /// Announces a session on the local network, once a second, over UDP broadcast.
    /// <para>
    /// This is what lets a guest pick a session out of a list instead of being told an
    /// address. Asking two people to exchange an IP and a port before they can play is a
    /// real barrier, and the information is discoverable.
    /// </para>
    /// <para>
    /// <b>Broadcast reaches the local network and nothing beyond it.</b> That is a feature
    /// here: a session is announced to the people on your own network and to nobody else.
    /// Playing with someone further away needs Steam, which is the next piece of work.
    /// </para>
    /// </summary>
    public sealed class LobbyBeacon : IDisposable
    {
        /// <summary>
        /// The port the announcement goes to. Fixed, because a guest listening has to know
        /// where to listen, and nothing is negotiated before discovery by definition.
        /// </summary>
        public const int DiscoveryPort = 7778;

        private UdpClient _socket;
        private float _sinceBroadcast;

        /// <summary>How often to announce, in seconds.</summary>
        public float Interval { get; set; } = 1.0f;

        public bool IsRunning => _socket != null;

        /// <summary>Set by the owner; sent on the next tick. Null stops the announcements.</summary>
        public Func<LobbyBeaconV1> Describe { get; set; }

        /// <summary>Reports a failure once, so a blocked socket is visible but not spam.</summary>
        public Action<string> Log { get; set; }

        private bool _warned;

        public void Start()
        {
            if (_socket != null) return;

            try
            {
                _socket = new UdpClient { EnableBroadcast = true };

                // Bind to anything free: this socket only sends. Binding to the discovery
                // port here would stop a second instance on the same machine from
                // listening on it, which is exactly the two-copies-one-PC test setup.
                _socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            catch (Exception ex)
            {
                _socket = null;
                Log?.Invoke($"Lobby: cannot announce on the network ({ex.Message}). " +
                            $"Guests will need the address typed in.");
            }
        }

        public void Stop()
        {
            try { _socket?.Close(); } catch (Exception) { }
            _socket = null;
            _sinceBroadcast = 0f;
            _warned = false;
        }

        /// <summary>Call every frame while hosting.</summary>
        public void Tick(float deltaTime)
        {
            if (_socket == null || Describe == null) return;

            _sinceBroadcast += deltaTime;
            if (_sinceBroadcast < Interval) return;
            _sinceBroadcast = 0f;

            try
            {
                var beacon = Describe();
                if (beacon == null) return;

                var bytes = beacon.Serialise();
                _socket.Send(bytes, bytes.Length,
                             new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Log?.Invoke($"Lobby: announcement failed ({ex.Message}). " +
                            $"The session still works; it just will not be found automatically.");
            }
        }

        public void Dispose() => Stop();
    }
}
