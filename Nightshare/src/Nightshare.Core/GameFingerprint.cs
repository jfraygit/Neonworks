using System;
using System.Collections.Generic;
using System.IO;

namespace Nightshare.Core
{
    /// <summary>
    /// Identifies which build of Nivalis Nights is installed, so two peers refuse to play
    /// together across a game patch.
    /// <para>
    /// This matters more than it sounds. A game patch moves IL2CPP method offsets and can
    /// change save packet layouts, so mismatched peers would not fail cleanly: they would
    /// connect, appear to work, and corrupt each other's state.
    /// </para>
    /// <para>
    /// <b>This lives in Core on purpose.</b> The plugin and the fake peer must compute the
    /// id the same way or they will disagree about identical installs and every join will
    /// be refused. That exact bug happened once already, when the plugin used file sizes
    /// and the fake peer still claimed a hardcoded Steam build id.
    /// </para>
    /// <para>
    /// The id is the byte lengths of <c>GameAssembly.dll</c> and <c>global-metadata.dat</c>.
    /// Deliberately not file timestamps, which differ per machine by download time and
    /// would make two identical installs look different. Deliberately not the Steam build
    /// id either, which is not reachable from inside the game process.
    /// </para>
    /// </summary>
    public static class GameFingerprint
    {
        public const string Unknown = "unknown";

        private const string AssemblyName = "GameAssembly.dll";
        private const string DataFolder = "Nivalis Nights_Data";

        /// <summary>
        /// Fingerprint the install at <paramref name="gameRoot"/>, or <see cref="Unknown"/>
        /// if the files are not there.
        /// </summary>
        public static string Compute(string gameRoot)
        {
            if (string.IsNullOrWhiteSpace(gameRoot)) return Unknown;

            try
            {
                var assembly = new FileInfo(Path.Combine(gameRoot, AssemblyName));
                var metadata = new FileInfo(Path.Combine(
                    gameRoot, DataFolder, "il2cpp_data", "Metadata", "global-metadata.dat"));

                if (!assembly.Exists || !metadata.Exists) return Unknown;

                return $"{assembly.Length}-{metadata.Length}";
            }
            catch (Exception)
            {
                return Unknown;
            }
        }

        /// <summary>
        /// Find the install without being told where it is. Used by the fake peer, which
        /// has no game process to ask. Returns null when it cannot be found.
        /// </summary>
        public static string FindGameDirectory()
        {
            foreach (var dir in CandidateDirectories())
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, AssemblyName))) return dir;
                }
                catch (Exception)
                {
                    // An unreadable drive is not a reason to give up on the rest.
                }
            }
            return null;
        }

        /// <summary>Locate the install and fingerprint it in one step.</summary>
        public static string AutoDetect()
        {
            var dir = FindGameDirectory();
            return dir == null ? Unknown : Compute(dir);
        }

        public static bool IsUnknown(string fingerprint) =>
            string.IsNullOrEmpty(fingerprint) || fingerprint == Unknown;

        private static IEnumerable<string> CandidateDirectories()
        {
            const string relative = @"steamapps\common\Nivalis Nights";

            var steamRoots = new List<string>();

            foreach (var envVar in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                var pf = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(pf)) steamRoots.Add(Path.Combine(pf, "Steam"));
            }

            // Other Steam libraries, listed in libraryfolders.vdf.
            foreach (var root in new List<string>(steamRoots))
            {
                foreach (var extra in ReadLibraryFolders(Path.Combine(root, "steamapps", "libraryfolders.vdf")))
                    steamRoots.Add(extra);
            }

            foreach (var root in steamRoots)
                yield return Path.Combine(root, relative);

            // Bare drive roots, for a library at D:\SteamLibrary or similar.
            foreach (var drive in new[] { "C", "D", "E", "F" })
            {
                yield return $@"{drive}:\SteamLibrary\{relative}";
                yield return $@"{drive}:\Steam\{relative}";
                yield return $@"{drive}:\Games\Nivalis Nights";
            }
        }

        /// <summary>
        /// Pull library paths out of libraryfolders.vdf. Deliberately a loose scan rather
        /// than a VDF parser: the format has changed between Steam versions and the cost of
        /// a miss is only that one candidate path is not tried.
        /// </summary>
        private static IEnumerable<string> ReadLibraryFolders(string vdfPath)
        {
            string[] lines;
            try
            {
                if (!File.Exists(vdfPath)) yield break;
                lines = File.ReadAllLines(vdfPath);
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = trimmed.Split('"');
                if (parts.Length < 4) continue;

                var path = parts[3].Replace(@"\\", @"\");
                if (!string.IsNullOrWhiteSpace(path)) yield return path;
            }
        }
    }
}
