using System;

namespace Nightshare.Core.Diagnostics
{
    /// <summary>
    /// Fingerprints a byte payload so two peers can compare state cheaply.
    /// <para>
    /// FNV-1a 64 bit. Not cryptographic and not trying to be: the adversary here is a
    /// replication bug, not an attacker. What matters is that it is stable across
    /// processes and machines, which rules out <c>string.GetHashCode</c> and anything else
    /// randomised per process.
    /// </para>
    /// </summary>
    public static class StateHash
    {
        private const ulong Offset = 14695981039346656037;
        private const ulong Prime = 1099511628211;

        public static ulong Compute(byte[] data)
        {
            if (data == null) return 0;

            var hash = Offset;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= Prime;
            }
            return hash;
        }

        public static ulong Compute(byte[] data, int offset, int count)
        {
            if (data == null) return 0;
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            var hash = Offset;
            for (int i = offset; i < offset + count; i++)
            {
                hash ^= data[i];
                hash *= Prime;
            }
            return hash;
        }

        /// <summary>Short stable form for logs.</summary>
        public static string Format(ulong hash) => hash.ToString("X16");
    }
}
