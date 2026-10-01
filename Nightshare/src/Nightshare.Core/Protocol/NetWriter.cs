using System;
using System.IO;
using System.Text;

namespace Nightshare.Core.Protocol
{
    /// <summary>
    /// Builds a message payload. Little-endian, length-prefixed strings, no reflection.
    /// <para>
    /// Deliberately explicit rather than clever. Every field is written by hand so the
    /// wire format is readable in one place and a change to it is visible in a diff.
    /// </para>
    /// </summary>
    public sealed class NetWriter : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryWriter _writer;

        public NetWriter(MessageType type)
        {
            Type = type;
            _stream = new MemoryStream(256);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
            _writer.Write((ushort)type);
        }

        public MessageType Type { get; }

        public NetWriter Write(bool v) { _writer.Write(v); return this; }
        public NetWriter Write(byte v) { _writer.Write(v); return this; }
        public NetWriter Write(short v) { _writer.Write(v); return this; }
        public NetWriter Write(ushort v) { _writer.Write(v); return this; }
        public NetWriter Write(int v) { _writer.Write(v); return this; }
        public NetWriter Write(uint v) { _writer.Write(v); return this; }
        public NetWriter Write(long v) { _writer.Write(v); return this; }
        public NetWriter Write(ulong v) { _writer.Write(v); return this; }
        public NetWriter Write(float v) { _writer.Write(v); return this; }
        public NetWriter Write(double v) { _writer.Write(v); return this; }

        /// <summary>Null is written as empty. The protocol never distinguishes the two.</summary>
        public NetWriter Write(string v) { _writer.Write(v ?? string.Empty); return this; }

        public NetWriter Write(PeerId v) { _writer.Write(v.Value.ToByteArray()); return this; }

        public NetWriter WriteBytes(byte[] v)
        {
            if (v == null) { _writer.Write(0); return this; }
            _writer.Write(v.Length);
            _writer.Write(v);
            return this;
        }

        /// <summary>Three floats. Avoids a UnityEngine dependency in Core.</summary>
        public NetWriter WriteVector3(float x, float y, float z)
        {
            _writer.Write(x); _writer.Write(y); _writer.Write(z);
            return this;
        }

        /// <summary>Four floats.</summary>
        public NetWriter WriteQuaternion(float x, float y, float z, float w)
        {
            _writer.Write(x); _writer.Write(y); _writer.Write(z); _writer.Write(w);
            return this;
        }

        /// <summary>Finish the message. The writer must not be used afterwards.</summary>
        public byte[] ToArray()
        {
            _writer.Flush();
            return _stream.ToArray();
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _stream?.Dispose();
        }
    }
}
