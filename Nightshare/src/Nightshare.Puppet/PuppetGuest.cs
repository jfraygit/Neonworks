using System;
using Nightshare.Core;
using Nightshare.Core.Protocol;
using Nightshare.Core.Session;
using Nightshare.Core.Transport;

namespace Nightshare.Puppet
{
    /// <summary>
    /// A guest with no game behind it.
    /// <para>
    /// Joins with <see cref="NightshareSession"/>, so the handshake it runs is the one the
    /// plugin runs. Once welcomed it sends <see cref="PlayerTransformV1"/> along
    /// <see cref="Route"/>. The plugin already turns that stream into a cloned NPC.
    /// </para>
    /// <para>
    /// <b>It never sends <see cref="SaveRequestV1"/>.</b> That message makes the host
    /// capture the live save and ship it. A puppet has no world to load the file into, and
    /// capturing a save out from under a running city is the risk the readme warns about.
    /// Players and the clock replicate without it. The world does not, which is the point
    /// of a body test.
    /// </para>
    /// <para>
    /// <see cref="ModVersion"/> has to match <c>NightshareVersion</c> in
    /// <c>Directory.Build.props</c>. The handshake refuses anything else.
    /// </para>
    /// </summary>
    public sealed class PuppetGuest : IDisposable
    {
        /// <summary>Keep in step with NightshareVersion. The plugin sends the same string.</summary>
        public const string ModVersion = "0.1.0";

        private readonly NightshareSession _session = new NightshareSession();

        public GuestRoute Route { get; }
        public NightshareSession Session => _session;
        public int TransformsSent { get; private set; }

        /// <summary>Set when the session fails. Safe to show.</summary>
        public string Failure { get; private set; }

        public event Action<string> Log;

        public PuppetGuest(GuestRoute route)
        {
            Route = route ?? new GuestRoute();
            _session.Log += message => Log?.Invoke(message);
            _session.SessionFailed += reason => Failure = reason;
            _session.MessageReceived += OnMessage;
        }

        public void Join(string endpoint, string playerName, string gameBuildId)
        {
            if (GameFingerprint.IsUnknown(gameBuildId))
                throw new ArgumentException(
                    "A puppet needs the same game fingerprint as the host. " +
                    "Pass the id the plugin logged, or let it autodetect the install.",
                    nameof(gameBuildId));

            _session.Join(new TcpTransport(), endpoint, new SessionIdentity
            {
                PlayerName = string.IsNullOrWhiteSpace(playerName) ? "Puppet" : playerName,
                ModVersion = ModVersion,
                GameBuildId = gameBuildId,
            });
        }

        public void Pump() => _session.Pump();

        /// <summary>
        /// Send one position sample. Returns false until the session is up and the route
        /// has a centre, in which case nothing is put on the wire.
        /// </summary>
        public bool SendTransform(float elapsedSeconds)
        {
            if (!_session.IsActive || !Route.HasCenter) return false;

            Route.Sample(elapsedSeconds, out var x, out var y, out var z, out var yaw, out var moving);

            // The id in the payload must be the one the host assigned. The plugin drops a
            // transform whose peer does not match the socket it arrived on.
            var payload = new PlayerTransformV1
            {
                Peer = _session.LocalPeer,
                X = x,
                Y = y,
                Z = z,
                Yaw = yaw,
                IsMoving = moving,
            }.Serialise();

            _session.SendToHost(payload, DeliveryMode.Unreliable);
            TransformsSent++;
            return true;
        }

        private void OnMessage(PeerId from, NetReader reader)
        {
            if (Route.HasCenter) return;
            if (reader.Type != MessageType.PlayerTransformV1) return;

            var msg = PlayerTransformV1.Parse(reader);
            if (!msg.Peer.IsHost) return;

            Route.SetCenter(msg.X, msg.Y, msg.Z);
            Log?.Invoke(
                $"Anchored on the host at ({msg.X:0.0}, {msg.Y:0.0}, {msg.Z:0.0}). " +
                $"Walking a {Route.Radius:0.0} m circle at {Route.Speed:0.0} m/s.");
        }

        public void Dispose()
        {
            try { _session.Dispose(); } catch { }
        }
    }
}
