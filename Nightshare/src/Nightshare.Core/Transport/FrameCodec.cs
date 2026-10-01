using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Nightshare.Core.Transport
{
    /// <summary>
    /// Length-prefixed framing over a stream.
    /// <para>
    /// TCP is a byte stream, not a message stream: one Send is not one Receive. Every
    /// payload is prefixed with its length so the reader can reassemble message
    /// boundaries. Forgetting this is the classic first networking bug and it presents as
    /// "works on loopback, corrupts over LAN", because only a real network splits packets.
    /// </para>
    /// </summary>
    public static class FrameCodec
    {
        /// <summary>
        /// Largest payload we will send or accept, 16 MB. The biggest legitimate message is
        /// the world snapshot, measured at roughly 600 KB on a day-one save, so this has
        /// wide headroom while still refusing a garbage length prefix rather than trying to
        /// allocate 4 GB from it.
        /// </summary>
        public const int MaxFrameSize = 16 * 1024 * 1024;

        public const int HeaderSize = 4;

        /// <summary>Write a length-prefixed frame. Caller must serialise writes per stream.</summary>
        public static void WriteFrame(Stream stream, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length > MaxFrameSize)
                throw new InvalidDataException($"Frame of {payload.Length} bytes exceeds the {MaxFrameSize} byte limit.");

            var header = new byte[HeaderSize];
            header[0] = (byte)(payload.Length & 0xFF);
            header[1] = (byte)((payload.Length >> 8) & 0xFF);
            header[2] = (byte)((payload.Length >> 16) & 0xFF);
            header[3] = (byte)((payload.Length >> 24) & 0xFF);

            stream.Write(header, 0, HeaderSize);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        /// <summary>
        /// Read one whole frame, blocking until it arrives.
        /// Returns null on a clean end of stream.
        /// </summary>
        /// <exception cref="InvalidDataException">The length prefix was not believable.</exception>
        public static byte[] ReadFrame(Stream stream, CancellationToken token = default)
        {
            var header = new byte[HeaderSize];
            if (!ReadExactly(stream, header, HeaderSize, token)) return null;

            int length = header[0]
                       | (header[1] << 8)
                       | (header[2] << 16)
                       | (header[3] << 24);

            if (length < 0 || length > MaxFrameSize)
                throw new InvalidDataException($"Refusing a frame length of {length} bytes. Stream is desynchronised or not Nightshare.");

            if (length == 0) return Array.Empty<byte>();

            var payload = new byte[length];
            if (!ReadExactly(stream, payload, length, token))
                throw new EndOfStreamException($"Stream ended {length} bytes into a frame.");

            return payload;
        }

        /// <summary>
        /// Fill <paramref name="count"/> bytes, looping over partial reads.
        /// A single Read is free to return fewer bytes than asked for, including 1.
        /// </summary>
        private static bool ReadExactly(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            int offset = 0;
            while (offset < count)
            {
                if (token.IsCancellationRequested) return false;

                int read;
                try
                {
                    read = stream.Read(buffer, offset, count - offset);
                }
                catch (IOException)
                {
                    return false;      // socket closed under us
                }
                catch (ObjectDisposedException)
                {
                    return false;      // we closed it
                }
                catch (SocketException)
                {
                    return false;
                }

                if (read <= 0) return false;   // clean EOF
                offset += read;
            }
            return true;
        }
    }
}
