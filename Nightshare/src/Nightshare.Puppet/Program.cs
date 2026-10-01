using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Nightshare.Core;

namespace Nightshare.Puppet
{
    /// <summary>
    /// Headless guest. The other peer is a running game that has pressed Host.
    /// </summary>
    internal static class Program
    {
        private static bool _stop;

        public static int Main(string[] args)
        {
            if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?"))
            {
                Usage();
                return 0;
            }

            var endpoint = Value(args, "--endpoint") ?? "127.0.0.1:7777";
            var name = Value(args, "--name") ?? "Puppet";
            var build = Value(args, "--build");
            if (string.IsNullOrWhiteSpace(build))
            {
                build = GameFingerprint.AutoDetect();
                Console.WriteLine("Game fingerprint: " + build);
            }

            if (GameFingerprint.IsUnknown(build))
            {
                Console.Error.WriteLine(
                    "Could not fingerprint the Nivalis Nights install. " +
                    "Pass --build with the id the host logged, or install the game where " +
                    "GameFingerprint looks (the Steam library, or D:\\Games\\Nivalis Nights).");
                return 1;
            }

            var route = new GuestRoute
            {
                Radius = ParseFloat(Value(args, "--radius"), 4f),
                Speed = ParseFloat(Value(args, "--speed"), 3f),
            };

            if (Has(args, "--x") || Has(args, "--y") || Has(args, "--z"))
            {
                route.SetCenter(
                    ParseFloat(Value(args, "--x"), 0f),
                    ParseFloat(Value(args, "--y"), 0f),
                    ParseFloat(Value(args, "--z"), 0f));
            }

            var rate = ParseFloat(Value(args, "--rate"), 15f);
            if (rate < 1f) rate = 1f;

            using var guest = new PuppetGuest(route);
            guest.Log += message => Console.WriteLine(message);

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                _stop = true;
            };

            try
            {
                Console.WriteLine($"Joining {endpoint} as '{name}', build {build}.");
                guest.Join(endpoint, name, build);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            var interval = TimeSpan.FromSeconds(1.0 / rate);
            var send = Stopwatch.StartNew();
            var walk = new Stopwatch();
            var nextWaitLog = TimeSpan.FromSeconds(2);

            while (!_stop)
            {
                guest.Pump();

                if (guest.Failure != null)
                {
                    Console.Error.WriteLine(guest.Failure);
                    return 1;
                }

                if (!guest.Session.IsActive)
                {
                    Thread.Sleep(5);
                    continue;
                }

                if (!guest.Route.HasCenter)
                {
                    if (send.Elapsed >= nextWaitLog)
                    {
                        Console.WriteLine(
                            "Waiting for the host's position. Stand in the city, or pass --x --y --z.");
                        nextWaitLog += TimeSpan.FromSeconds(2);
                    }

                    Thread.Sleep(5);
                    continue;
                }

                // Elapsed time starts at the first send. Adding only the gap of the send
                // frame would walk at a fraction of --speed, because the loop ticks faster
                // than the send rate.
                if (!walk.IsRunning || send.Elapsed >= interval)
                {
                    if (!walk.IsRunning) walk.Start();
                    guest.SendTransform((float)walk.Elapsed.TotalSeconds);
                    send.Restart();

                    if (guest.TransformsSent == 1 || guest.TransformsSent % 150 == 0)
                        Console.WriteLine($"Sent position #{guest.TransformsSent}.");
                }

                Thread.Sleep(5);
            }

            Console.WriteLine("Left.");
            return 0;
        }

        private static void Usage()
        {
            Console.WriteLine(
                "Nightshare.Puppet — a guest with no game behind it.\n" +
                "\n" +
                "  dotnet run --project src/Nightshare.Puppet -- [options]\n" +
                "\n" +
                "  --endpoint <host:port>   Default 127.0.0.1:7777, the plugin's default.\n" +
                "  --name <name>            Default Puppet.\n" +
                "  --build <fingerprint>    Default: file sizes of the local install.\n" +
                "  --radius <metres>        Default 4.\n" +
                "  --speed <m/s>            Default 3, which is a full walk on the avatar.\n" +
                "  --rate <Hz>              Default 15, the plugin's send rate.\n" +
                "  --x --y --z              Walk around this point instead of the host.\n" +
                "\n" +
                "Does not request the host's save, and does not write a plugin into the game.\n" +
                "Host first (F9 once a city is loaded), then run this.");
        }

        private static bool Has(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Value(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                var value = args[i + 1];
                if (value != null && value.StartsWith("--", StringComparison.Ordinal)) return null;
                return value;
            }
            return null;
        }

        private static float ParseFloat(string text, float fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return value;
            return fallback;
        }
    }
}
