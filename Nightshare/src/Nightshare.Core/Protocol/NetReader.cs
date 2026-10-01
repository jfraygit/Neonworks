using System;
using System.IO;
using System.Text;

namespace Nightshare.Core.Protocol
{
    /// <summary>
    /// Reads a message payload written by <see cref="NetWriter"/>.
    /// <para>
    /// Every read is bounds checked and throws <see cref="MalformedMessageException"/>
    /// rather than returning junk. A peer sending a malformed message is either running a
    /// different version or is not Nightshare at all; in both cases the connection should
    /// end, not limp on with half-parsed state.
    /// </para>
    /// </summary>
    public sealed class NetReader : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;

        public NetReader(byte[] payload)
        {
            if (payload == null || payload.Length < sizeof(ushort))
                throw new MalformedMessageException("Message is shorter than its own type header.");

            _stream = new MemoryStream(payload, writable: false);
            _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

            var raw = _reader.ReadUInt16();
            Type = (MessageType)raw;
            RawType = raw;
        }

        /// <summary>Decoded type. May be a value this build does not know.</summary>
        public MessageType Type { get; }

        /// <summary>The wire value, for logging an unknown type usefully.</summary>
        public ushort RawType { get; }

        /// <summary>True when this build has no handler for the type at all.</summary>
        public bool IsUnknownType => !Enum.IsDefined(typeof(MessageType), Type);

        public bool ReadBool() => Guard(() => _reader.ReadBoolean());
        public byte ReadByte() => Guard(() => _reader.ReadByte());
        public short ReadInt16() => Guard(() => _reader.ReadInt16());
        public ushort ReadUInt16() => Guard(() => _reader.ReadUInt16());
        public int ReadInt32() => Guard(() => _reader.ReadInt32());
        public uint ReadUInt32() => Guard(() => _reader.ReadUInt32());
        public long ReadInt64() => Guard(() => _reader.ReadInt64());
        public ulong ReadUInt64() => Guard(() => _reader.ReadUInt64());
        public float ReadSingle() => Guard(() => _reader.ReadSingle());
        public double ReadDouble() => Guard(() => _reader.ReadDouble());
        public string ReadString() => Guard(() => _reader.ReadString());

        public PeerId ReadPeerId() => Guard(() => new PeerId(new Guid(_reader.ReadBytes(16))));

        public byte[] ReadBytes()
        {
            var length = ReadInt32();
            if (length < 0 || length > _stream.Length - _stream.Position)
                throw new MalformedMessageException($"Byte array length {length} runs past the end of the message.");
            return Guard(() => _reader.ReadBytes(length));
        }

        public (float x, float y, float z) ReadVector3() =>
            (ReadSingle(), ReadSingle(), ReadSingle());

        public (float x, float y, float z, float w) ReadQuaternion() =>
            (ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

        /// <summary>
        /// Bytes left unread. A non-zero value after parsing a message means the sender
        /// and receiver disagree about its layout, which is worth failing on.
        /// </summary>
        public long Remaining => _stream.Length - _stream.Position;

        /// <summary>
        /// Assert the message was consumed exactly. Call at the end of every parse.
        /// </summary>
        public void ExpectConsumed()
        {
            if (Remaining != 0)
                throw new MalformedMessageException(
                    $"{Type} left {Remaining} unread byte(s). Sender and receiver disagree about its layout.");
        }

        private T Guard<T>(Func<T> read)
        {
            try { return read(); }
            catch (EndOfStreamException) { throw new MalformedMessageException($"{Type} ended early."); }
            catch (ArgumentException ex) { throw new MalformedMessageException($"{Type} is malformed: {ex.Message}"); }
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _stream?.Dispose();
        }
    }

    /// <summary>A peer sent something this build cannot parse. Always fatal to the connection.</summary>
    public sealed class MalformedMessageException : Exception
    {
        public MalformedMessageException(string message) : base(message) { }
    }
}
