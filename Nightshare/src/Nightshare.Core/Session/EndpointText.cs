namespace Nightshare.Core.Session
{
    /// <summary>
    /// Splitting and rebuilding a <c>host:port</c> string.
    /// <para>
    /// Lives in Core, away from Unity, because the menu lets a player type both halves and
    /// a wrong split is the kind of thing that fails quietly: a port parsed into the address
    /// produces a connection attempt to a host that does not exist, which looks exactly like
    /// the other side not listening.
    /// </para>
    /// </summary>
    public static class EndpointText
    {
        public const int DefaultPort = 7777;

        /// <summary>
        /// The address half. The whole string when there is no port on it, so a player who
        /// types a bare address does not lose it.
        /// </summary>
        public static string Address(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return "";

            var text = endpoint.Trim();

            // LastIndexOf, not IndexOf. An IPv6 literal is full of colons and only the last
            // one can be the port separator.
            var colon = text.LastIndexOf(':');
            return colon > 0 ? text.Substring(0, colon) : text;
        }

        /// <summary>The port half, or <see cref="DefaultPort"/> when there is not a usable one.</summary>
        public static int Port(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return DefaultPort;

            var text = endpoint.Trim();
            var colon = text.LastIndexOf(':');
            if (colon < 0 || colon >= text.Length - 1) return DefaultPort;

            return int.TryParse(text.Substring(colon + 1), out var port) && IsUsablePort(port)
                ? port
                : DefaultPort;
        }

        /// <summary>Put the two halves back together, falling back on anything unusable.</summary>
        public static string Format(string address, int port)
        {
            var host = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address.Trim();
            var p = IsUsablePort(port) ? port : DefaultPort;
            return $"{host}:{p}";
        }

        /// <summary>
        /// Port 0 means "any free port" to the operating system, which is never what someone
        /// typing into a join field meant.
        /// </summary>
        public static bool IsUsablePort(int port) => port > 0 && port <= 65535;

        /// <summary>
        /// Whether an address is worth trying to connect to.
        /// <para>
        /// <b>Deliberately stricter than the framework's parser.</b> <c>IPAddress.Parse</c>
        /// accepts "7" and hands back 0.0.0.7, which is a real address that no session will
        /// ever be on. A half-typed address is indistinguishable from a complete one to the
        /// parser, and the only moment it can be caught usefully is while the person who
        /// typed it is still looking at it.
        /// </para>
        /// <para>
        /// Host names are allowed through on their presence of a letter: resolving them
        /// needs DNS and that is a connection's job, not a text field's.
        /// </para>
        /// </summary>
        public static bool LooksConnectable(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;

            var text = address.Trim();

            // Anything with a letter is a host name; let the connection resolve it.
            foreach (var c in text)
            {
                if (char.IsLetter(c)) return true;
            }

            // Otherwise it should be four dotted parts, each 0 to 255.
            var parts = text.Split('.');
            if (parts.Length != 4) return false;

            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 3) return false;
                if (!int.TryParse(part, out var n) || n < 0 || n > 255) return false;
            }

            return true;
        }
    }
}
