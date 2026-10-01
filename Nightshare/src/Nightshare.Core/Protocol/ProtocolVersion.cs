using System;

namespace Nightshare.Core.Protocol
{
    /// <summary>
    /// Compatibility rules for a session.
    /// <para>
    /// Three things must line up before two peers may play together: the wire protocol,
    /// the mod version and the game build. A mismatch on any of them is refused at the
    /// handshake with a reason the player can act on. The alternative is a peer that
    /// misparses a payload and corrupts a save, which is far worse than not connecting.
    /// </para>
    /// </summary>
    public static class ProtocolVersion
    {
        /// <summary>
        /// Bump on ANY change to an existing message's layout, and whenever a message is
        /// removed or its meaning changes. Adding a brand new message type does not
        /// require a bump, because an older peer simply never receives it.
        /// <para>
        /// When you bump this, also rename the affected message type to carry its new
        /// version (HelloV1 becomes HelloV2) so an old handler cannot silently accept new
        /// bytes into an old shape.
        /// </para>
        /// </summary>
        public const int Current = 1;

        /// <summary>
        /// Oldest protocol this build can still talk to. Equal to <see cref="Current"/>
        /// until there is a shipped version worth supporting.
        /// </summary>
        public const int MinimumSupported = 1;

        /// <summary>
        /// The Steam build this mod was first developed against, for the record only.
        /// <para>
        /// <b>Never send this as a peer's game build id.</b> Use
        /// <see cref="GameFingerprint"/>, which both the plugin and the fake peer compute
        /// the same way. Claiming this constant instead is what made the fake peer and the
        /// real game refuse each other over an identical install.
        /// </para>
        /// </summary>
        public const string DevelopedAgainstSteamBuild = "25603526";

        public static bool IsProtocolSupported(int protocol) =>
            protocol >= MinimumSupported && protocol <= Current;

        /// <summary>
        /// Decide whether a joining peer is compatible.
        /// Returns null when it is, or a player-facing reason when it is not.
        /// </summary>
        public static string CheckCompatibility(int protocol, string modVersion, string gameBuildId,
                                                string localModVersion, string localGameBuildId)
        {
            if (!IsProtocolSupported(protocol))
            {
                return protocol > Current
                    ? $"Their Nightshare protocol is newer (v{protocol}, this build speaks v{Current}). Update Nightshare."
                    : $"Their Nightshare protocol is too old (v{protocol}, this build needs at least v{MinimumSupported}). They should update Nightshare.";
            }

            if (!string.Equals(modVersion, localModVersion, StringComparison.Ordinal))
                return $"Nightshare versions differ: they have {modVersion}, this is {localModVersion}. Both players need the same version.";

            if (!string.Equals(gameBuildId, localGameBuildId, StringComparison.Ordinal))
                return $"Nivalis Nights versions differ: they are on build {gameBuildId}, this is build {localGameBuildId}. Both players need the same game version.";

            return null;
        }
    }
}
