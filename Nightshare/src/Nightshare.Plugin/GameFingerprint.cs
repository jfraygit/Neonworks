using BepInEx;

namespace Nightshare
{
    /// <summary>
    /// The running game's build fingerprint.
    /// <para>
    /// A thin wrapper over <see cref="Core.GameFingerprint"/>. The computation lives in
    /// Core so the plugin and the fake peer cannot disagree about the same install, which
    /// they did once, when this file had its own copy of the logic.
    /// </para>
    /// </summary>
    public static class GameFingerprint
    {
        private static string _cached;

        public static string Current
        {
            get
            {
                if (_cached != null) return _cached;

                // The game process always knows where it is, so no searching needed here.
                _cached = Core.GameFingerprint.Compute(Paths.GameRootPath);
                return _cached;
            }
        }

        public static bool IsUnknown => Core.GameFingerprint.IsUnknown(Current);
    }
}
