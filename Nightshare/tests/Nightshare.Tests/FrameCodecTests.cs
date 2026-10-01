using System;
using System.IO;
using Nightshare.Core.Transport;
using Xunit;

namespace Nightshare.Tests
{
    public class FrameCodecTests
    {
        [Fact]
        public void RoundTripsAPayload()
        {
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            using var ms = new MemoryStream();

            FrameCodec.WriteFrame(ms, payload);
            ms.Position = 0;

            Assert.Equal(payload, FrameCodec.ReadFrame(ms));
        }

        [Fact]
        public void RoundTripsAnEmptyPayload()
        {
            using var ms = new MemoryStream();
            FrameCodec.WriteFrame(ms, Array.Empty<byte>());
            ms.Position = 0;

            Assert.Empty(FrameCodec.ReadFrame(ms));
        }

        [Fact]
        public void ReadsBackToBackFramesIndependently()
        {
            using var ms = new MemoryStream();
            FrameCodec.WriteFrame(ms, new byte[] { 0xAA });
            FrameCodec.WriteFrame(ms, new byte[] { 0xBB, 0xCC });
            FrameCodec.WriteFrame(ms, new byte[] { 0xDD });
            ms.Position = 0;

            Assert.Equal(new byte[] { 0xAA }, FrameCodec.ReadFrame(ms));
            Assert.Equal(new byte[] { 0xBB, 0xCC }, FrameCodec.ReadFrame(ms));
            Assert.Equal(new byte[] { 0xDD }, FrameCodec.ReadFrame(ms));
        }

        /// <summary>
        /// The bug this whole class exists to prevent. A real socket splits a payload
        /// across reads; loopback usually does not, which is why the defect classically
        /// only shows up over a LAN.
        /// </summary>
        [Fact]
        public void ReassemblesAPayloadSplitAcrossReads()
        {
            var payload = new byte[5000];
            new Random(42).NextBytes(payload);

            using var full = new MemoryStream();
            FrameCodec.WriteFrame(full, payload);

            using var dribbling = new DribblingStream(full.ToArray(), bytesPerRead: 7);
            Assert.Equal(payload, FrameCodec.ReadFrame(dribbling));
        }

        [Fact]
        public void ReturnsNullOnCleanEndOfStream()
        {
            using var ms = new MemoryStream(Array.Empty<byte>());
            Assert.Null(FrameCodec.ReadFrame(ms));
        }

        [Fact]
        public void RejectsAnImplausibleLengthPrefixRatherThanAllocating()
        {
            // 0x7FFFFFFF as a little-endian length prefix.
            using var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
            Assert.Throws<InvalidDataException>(() => FrameCodec.ReadFrame(ms));
        }

        [Fact]
        public void RejectsANegativeLengthPrefix()
        {
            using var ms = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x80 });
            Assert.Throws<InvalidDataException>(() => FrameCodec.ReadFrame(ms));
        }

        [Fact]
        public void RefusesToWriteAnOversizedFrame()
        {
            using var ms = new MemoryStream();
            var huge = new byte[FrameCodec.MaxFrameSize + 1];
            Assert.Throws<InvalidDataException>(() => FrameCodec.WriteFrame(ms, huge));
        }

        [Fact]
        public void ThrowsWhenAStreamEndsPartWayThroughAFrame()
        {
            using var full = new MemoryStream();
            FrameCodec.WriteFrame(full, new byte[100]);

            var truncated = full.ToArray()[..50];
            using var ms = new MemoryStream(truncated);

            Assert.Throws<EndOfStreamException>(() => FrameCodec.ReadFrame(ms));
        }

        /// <summary>A stream that never returns more than N bytes per Read, like a real socket.</summary>
        private sealed class DribblingStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _bytesPerRead;
            private int _position;

            public DribblingStream(byte[] data, int bytesPerRead)
            {
                _data = data;
                _bytesPerRead = bytesPerRead;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int remaining = _data.Length - _position;
                if (remaining <= 0) return 0;

                int n = Math.Min(Math.Min(count, _bytesPerRead), remaining);
                Array.Copy(_data, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }
}
