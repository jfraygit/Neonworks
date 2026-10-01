using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nightshare.Core;
using Nightshare.Core.Protocol;
using Nightshare.Core.Session;
using Nightshare.Core.Transport;
using Nightshare.Puppet;
using Xunit;

namespace Nightshare.Tests
{
    /// <summary>
    /// The fake peer against a real host session, on loopback. No game, no save.
    /// </summary>
    public class PuppetGuestTests : IDisposable
    {
        private const string Build = "72380928-12659680";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private readonly NightshareSession _host = new NightshareSession();
        private PuppetGuest _puppet;

        public void Dispose()
        {
            try { _puppet?.Dispose(); } catch { }
            try { _host.Dispose(); } catch { }
        }

        [Fact]
        public void AFullCircleReturnsToTheStartFacingTheSameWay()
        {
            var route = new GuestRoute { Radius = 4f, Speed = 3f };
            route.SetCenter(10f, 2f, -5f);

            var lap = (float)(Math.PI * 2 * route.Radius / route.Speed);

            route.Sample(0f, out var x0, out var y0, out var z0, out var yaw0, out var moving0);
            route.Sample(lap, out var x1, out var y1, out var z1, out var yaw1, out var moving1);

            Assert.True(moving0);
            Assert.True(moving1);
            Assert.Equal(14f, x0, 2);
            Assert.Equal(2f, y0, 2);
            Assert.Equal(-5f, z0, 2);
            Assert.Equal(0f, yaw0, 2);
            Assert.Equal(x0, x1, 2);
            Assert.Equal(y0, y1, 2);
            Assert.Equal(z0, z1, 2);
            Assert.Equal(yaw0, yaw1, 2);
        }

        [Fact]
        public void AQuarterLapFacesAlongTheTangent()
        {
            var route = new GuestRoute { Radius = 4f, Speed = 3f };
            route.SetCenter(10f, 2f, -5f);

            var quarter = (float)(Math.PI * 2 * route.Radius / route.Speed / 4.0);
            route.Sample(quarter, out var x, out var y, out var z, out var yaw, out var moving);

            Assert.True(moving);
            Assert.Equal(10f, x, 2);
            Assert.Equal(2f, y, 2);
            Assert.Equal(-1f, z, 2);
            Assert.Equal(-90f, yaw, 2);
        }

        [Fact]
        public void TheHostSeesThePuppetWalkAroundIt()
        {
            var port = FreePort();
            _host.Host(new TcpTransport(), port.ToString(), Identity("Hana"));

            var route = new GuestRoute { Radius = 4f, Speed = 3f };
            _puppet = new PuppetGuest(route);
            _puppet.Join($"127.0.0.1:{port}", "Puppet", Build);

            PumpUntil(() => _puppet.Session.IsActive && _host.Peers.Count == 1);

            _host.Broadcast(new PlayerTransformV1
            {
                Peer = PeerId.Host,
                X = 10f,
                Y = 2f,
                Z = -5f,
                Yaw = 48f,
                IsMoving = false,
            }.Serialise(), DeliveryMode.Unreliable);

            PumpUntil(() => route.HasCenter);
            Assert.Equal(10f, route.CenterX, 2);
            Assert.Equal(2f, route.CenterY, 2);
            Assert.Equal(-5f, route.CenterZ, 2);

            // A later host position must not drag the circle along behind them.
            _host.Broadcast(new PlayerTransformV1
            {
                Peer = PeerId.Host,
                X = 80f,
                Y = 0f,
                Z = 80f,
                Yaw = 0f,
                IsMoving = true,
            }.Serialise(), DeliveryMode.Unreliable);
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.Equal(10f, route.CenterX, 2);

            PlayerTransformV1 seen = null;
            PeerId seenFrom = PeerId.None;
            var saveRequests = 0;
            _host.MessageReceived += (from, reader) =>
            {
                if (reader.Type == MessageType.SaveRequestV1) saveRequests++;
                if (reader.Type != MessageType.PlayerTransformV1) return;
                seenFrom = from;
                seen = PlayerTransformV1.Parse(reader);
            };

            Assert.True(_puppet.SendTransform(0f));
            PumpUntil(() => seen != null);

            Assert.Equal(_puppet.Session.LocalPeer, seen.Peer);
            Assert.Equal(seenFrom, seen.Peer);
            Assert.Equal(14f, seen.X, 2);
            Assert.Equal(2f, seen.Y, 2);
            Assert.Equal(-5f, seen.Z, 2);
            Assert.True(seen.IsMoving);
            Assert.Equal(0, saveRequests);

            var quarter = (float)(Math.PI * 2 * route.Radius / route.Speed / 4.0);
            seen = null;
            Assert.True(_puppet.SendTransform(quarter));
            PumpUntil(() => seen != null);

            Assert.Equal(10f, seen.X, 2);
            Assert.Equal(-1f, seen.Z, 2);
            Assert.Equal(-90f, seen.Yaw, 1);
            Assert.Equal(0, saveRequests);
        }

        [Fact]
        public void ABuildMismatchIsStillRefused()
        {
            var port = FreePort();
            _host.Host(new TcpTransport(), port.ToString(), Identity("Hana"));

            _puppet = new PuppetGuest(new GuestRoute());
            _puppet.Join($"127.0.0.1:{port}", "Puppet", "1-1");

            PumpUntil(() => _puppet.Failure != null || _host.Peers.Count > 0);

            Assert.NotNull(_puppet.Failure);
            Assert.Contains("versions differ", _puppet.Failure);
            Assert.Empty(_host.Peers);
        }

        private static SessionIdentity Identity(string name) => new SessionIdentity
        {
            PlayerName = name,
            ModVersion = PuppetGuest.ModVersion,
            GameBuildId = Build,
        };

        private void PumpUntil(Func<bool> condition)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < Timeout)
            {
                _host.Pump();
                _puppet?.Pump();
                if (condition()) return;
                Thread.Sleep(5);
            }

            throw new TimeoutException($"Condition not met within {Timeout.TotalSeconds}s. Failure: {_puppet?.Failure}");
        }

        private void PumpFor(TimeSpan duration)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration)
            {
                _host.Pump();
                _puppet?.Pump();
                Thread.Sleep(5);
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
