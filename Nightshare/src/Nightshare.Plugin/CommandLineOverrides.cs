using System;

namespace Nightshare
{
    /// <summary>
    /// Command line switches that override the config file.
    /// <para>
    /// These exist because two instances of the game on one machine share a single
    /// <c>BepInEx/config</c> directory, so the host and the client cannot be told apart by
    /// configuration alone. Without this the launch scripts would need two game installs.
    /// </para>
    /// <para>
    /// Unity ignores switches it does not recognise, so these pass through harmlessly.
    /// Both <c>--nightshare-mode host</c> and <c>--nightshare-mode=host</c> work.
    /// </para>
    /// </summary>
    public static class CommandLineOverrides
    {
        private const string Prefix = "--nightshare-";

        private static bool _parsed;
        private static StartupMode? _mode;
        private static string _endpoint;
        private static string _playerName;
        private static bool? _recordTraces;

        public static StartupMode? Mode { get { Parse(); return _mode; } }
        public static string Endpoint { get { Parse(); return _endpoint; } }
        public static string PlayerName { get { Parse(); return _playerName; } }
        public static bool? RecordTraces { get { Parse(); return _recordTraces; } }

        /// <summary>True when any override was supplied, so it can be logged once.</summary>
        public static bool Any =>
            Mode.HasValue || Endpoint != null || PlayerName != null || RecordTraces.HasValue;

        public static string Describe()
        {
            if (!Any) return "none";

            var parts = new System.Collections.Generic.List<string>();
            if (_mode.HasValue) parts.Add($"mode={_mode}");
            if (_endpoint != null) parts.Add($"endpoint={_endpoint}");
            if (_playerName != null) parts.Add($"name={_playerName}");
            if (_recordTraces.HasValue) parts.Add($"record={_recordTraces}");
            return string.Join(" ", parts);
        }

        private static void Parse()
        {
            if (_parsed) return;
            _parsed = true;

            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return; }

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == null || !arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;

                string key, value;
                var eq = arg.IndexOf('=');
                if (eq > 0)
                {
                    key = arg.Substring(Prefix.Length, eq - Prefix.Length);
                    value = arg.Substring(eq + 1);
                }
                else
                {
                    key = arg.Substring(Prefix.Length);
                    value = i + 1 < args.Length ? args[++i] : null;
                }

                if (value == null) continue;
                value = value.Trim('"');

                switch (key.ToLowerInvariant())
                {
                    case "mode":
                        if (Enum.TryParse<StartupMode>(value, ignoreCase: true, out var m)) _mode = m;
                        break;
                    case "endpoint":
                        _endpoint = value;
                        break;
                    case "name":
                    case "playername":
                        _playerName = value;
                        break;
                    case "record":
                    case "recordtraces":
                        if (bool.TryParse(value, out var b)) _recordTraces = b;
                        break;
                }
            }
        }
    }
}
