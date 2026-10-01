using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nightshare.Core.Diagnostics
{
    public enum PacketDirection : byte
    {
        Inbound = 0,
        Outbound = 1,
    }

    /// <summary>One recorded message, with enough context to replay it in order.</summary>
    public readonly struct RecordedPacket
    {
        public RecordedPacket(long elapsedMs, PacketDirection direction, PeerId peer, byte[] payload)
        {
            ElapsedMs = elapsedMs;
            Direction = direction;
            Peer = peer;
            Payload = payload;
        }

        /// <summary>Milliseconds since recording began. Preserves relative timing on replay.</summary>
        public long ElapsedMs { get; }

        public PacketDirection Direction { get; }
        public PeerId Peer { get; }
        public byte[] Payload { get; }

        public ushort MessageType =>
            Payload != null && Payload.Length >= 2
                ? (ushort)(Payload[0] | (Payload[1] << 8))
                : (ushort)0;
    }

    /// <summary>
    /// Captures a session's traffic to a file and reads it back.
    /// <para>
    /// A desync that takes forty minutes of play to reproduce is close to undebuggable. A
    /// recording of the same session replays in seconds, deterministically, as many times
    /// as needed, and can be attached to a bug report.
    /// </para>
    /// <para>
    /// Format is deliberately simple and self-describing so a trace outlives the build
    /// that produced it. Header, then length-prefixed records.
    /// </para>
    /// </summary>
    public sealed class PacketRecorder : IDisposable
    {
        /// <summary>"NSTR", Nightshare trace.</summary>
        private const uint Magic = 0x5254534E;
        private const int FormatVersion = 1;

        private readonly BinaryWriter _writer;
        private readonly object _lock = new object();
        private bool _disposed;

        public PacketRecorder(Stream stream, string modVersion, string gameBuildId, bool isHost)
        {
            _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            _writer.Write(Magic);
            _writer.Write(FormatVersion);
            _writer.Write(modVersion ?? "");
            _writer.Write(gameBuildId ?? "");
            _writer.Write(isHost);
            PacketCount = 0;
        }

        public static PacketRecorder ToFile(string path, string modVersion, string gameBuildId, bool isHost)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            return new PacketRecorder(File.Create(path), modVersion, gameBuildId, isHost);
        }

        public int PacketCount { get; private set; }

        /// <summary>
        /// Record one message. Safe to call from any thread, since the transport's IO
        /// threads are a natural place to hook this.
        /// </summary>
        public void Record(long elapsedMs, PacketDirection direction, PeerId peer, byte[] payload)
        {
            if (_disposed) return;

            lock (_lock)
            {
                if (_disposed) return;

                _writer.Write(elapsedMs);
                _writer.Write((byte)direction);
                _writer.Write(peer.Value.ToByteArray());
                _writer.Write(payload?.Length ?? 0);
                if (payload is { Length: > 0 }) _writer.Write(payload);

                PacketCount++;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _writer.Flush(); } catch { }
                try { _writer.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Header of a recorded trace.</summary>
    public sealed class TraceInfo
    {
        public int FormatVersion { get; init; }
        public string ModVersion { get; init; }
        public string GameBuildId { get; init; }
        public bool IsHost { get; init; }
    }

    /// <summary>Reads a trace written by <see cref="PacketRecorder"/>.</summary>
    public static class PacketReplay
    {
        private const uint Magic = 0x5254534E;

        public static (TraceInfo info, IReadOnlyList<RecordedPacket> packets) Read(Stream stream)
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (r.ReadUInt32() != Magic)
                throw new InvalidDataException("Not a Nightshare trace file.");

            var info = new TraceInfo
            {
                FormatVersion = r.ReadInt32(),
                ModVersion = r.ReadString(),
                GameBuildId = r.ReadString(),
                IsHost = r.ReadBoolean(),
            };

            var packets = new List<RecordedPacket>();

            while (true)
            {
                long elapsed;
                try { elapsed = r.ReadInt64(); }
                catch (EndOfStreamException) { break; }

                var direction = (PacketDirection)r.ReadByte();
                var peer = new PeerId(new Guid(r.ReadBytes(16)));
                var length = r.ReadInt32();

                if (length < 0)
                    throw new InvalidDataException($"Trace declares a packet of {length} bytes.");

                var payload = length > 0 ? r.ReadBytes(length) : Array.Empty<byte>();
                if (payload.Length != length)
                    throw new InvalidDataException("Trace ends part way through a packet.");

                packets.Add(new RecordedPacket(elapsed, direction, peer, payload));
            }

            return (info, packets);
        }

        public static (TraceInfo info, IReadOnlyList<RecordedPacket> packets) ReadFile(string path)
            => Read(File.OpenRead(path));

        /// <summary>
        /// Summary for the console: how many of each message type, over what span.
        /// Usually the fastest way to see that a peer stopped sending something.
        /// </summary>
        public static string Summarise(TraceInfo info, IReadOnlyList<RecordedPacket> packets)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Nightshare trace v{info.FormatVersion}");
            sb.AppendLine($"  recorded by : {(info.IsHost ? "host" : "client")}");
            sb.AppendLine($"  mod version : {info.ModVersion}");
            sb.AppendLine($"  game build  : {info.GameBuildId}");
            sb.AppendLine($"  packets     : {packets.Count}");

            if (packets.Count == 0) return sb.ToString();

            sb.AppendLine($"  span        : {packets[packets.Count - 1].ElapsedMs} ms");
            sb.AppendLine();

            var counts = new Dictionary<(ushort type, PacketDirection dir), int>();
            foreach (var p in packets)
            {
                var key = (p.MessageType, p.Direction);
                counts.TryGetValue(key, out var n);
                counts[key] = n + 1;
            }

            sb.AppendLine("  by message type:");
            foreach (var kv in counts)
                sb.AppendLine($"    {kv.Key.dir,-8} type {kv.Key.type,-6} x{kv.Value}");

            return sb.ToString();
        }
    }
}
