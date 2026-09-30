using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lumen
{
    /// <summary>
    /// Asks GitHub once, in the background, whether a newer Lumen has been released.
    /// <para>
    /// One request at startup and never again. It runs off the main thread and only ever
    /// writes a string, which the panel reads when it draws - no Unity object is touched
    /// from the worker, because that is not safe.
    /// </para>
    /// <para>
    /// Every failure is silent by design. Being offline, rate limited, or behind a firewall
    /// is not a problem the player needs a popup about.
    /// </para>
    /// </summary>
    internal static class UpdateCheck
    {
        private const string ReleaseApi = "https://api.github.com/repos/jfraygit/Neonworks/releases/latest";

        internal const string ReleasesUrl = "github.com/jfraygit/Neonworks/releases";

        /// <summary>The newer version's tag, or null when up to date or unknown.</summary>
        internal static string NewerVersion { get; private set; }

        internal static void Start()
        {
            if (!LumenConfig.CheckForUpdates.Value) return;

            Task.Run(async () =>
            {
                try
                {
                    using (var client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(10);
                        client.DefaultRequestHeaders.Add("User-Agent", "Lumen/" + Build.Version);

                        string json = await client.GetStringAsync(ReleaseApi).ConfigureAwait(false);

                        // One field is wanted from a large document, and pulling in a JSON
                        // parser for it would be the larger cost.
                        var match = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                        if (!match.Success) return;

                        string tag = match.Groups[1].Value.TrimStart('v', 'V');
                        if (!Version.TryParse(tag, out var latest)) return;
                        if (!Version.TryParse(Build.Version, out var current)) return;

                        if (latest <= current) return;

                        NewerVersion = tag;
                        LumenPlugin.Log.LogInfo($"Lumen {tag} is available: https://{ReleasesUrl}");
                    }
                }
                catch (Exception)
                {
                    // Offline, rate limited, DNS blocked - all fine, all silent.
                }
            });
        }
    }
}
