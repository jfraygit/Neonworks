using BepInEx.Configuration;
using UnityEngine;

namespace Nightshare
{
    public enum StartupMode
    {
        /// <summary>Do nothing on launch. Use the hotkeys.</summary>
        Off = 0,

        /// <summary>Start hosting as soon as the game is ready. Used by the launch scripts.</summary>
        Host = 1,

        /// <summary>Connect as soon as the game is ready. Used by the launch scripts.</summary>
        Join = 2,
    }

    public enum TransportChoice
    {
        /// <summary>
        /// TCP over loopback or LAN. The only option that works for two copies on one
        /// machine, because both attach to the same Steam client and share a SteamID.
        /// </summary>
        Tcp = 0,

        /// <summary>Steam P2P. Real play. Not implemented yet.</summary>
        SteamP2P = 1,
    }

    /// <summary>
    /// Everything a player or a test script can change, surfaced in
    /// <c>BepInEx/config/dev.nightshare.nivalis.cfg</c>.
    /// </summary>
    public sealed class NightshareConfig
    {
        public ConfigEntry<StartupMode> Mode { get; }
        public ConfigEntry<TransportChoice> Transport { get; }
        public ConfigEntry<string> Endpoint { get; }
        public ConfigEntry<string> PlayerName { get; }

        public ConfigEntry<string> MenuKey { get; }
        public ConfigEntry<string> HostKey { get; }
        public ConfigEntry<string> JoinKey { get; }
        public ConfigEntry<string> LeaveKey { get; }
        public ConfigEntry<string> StatusKey { get; }
        public ConfigEntry<string> ProbeKey { get; }
        public ConfigEntry<string> SaveTestKey { get; }

        public ConfigEntry<float> WalkSpeed { get; }
        public ConfigEntry<float> SprintSpeed { get; }
        public ConfigEntry<bool> LogGaitChanges { get; }
        public ConfigEntry<bool> ShowAvatarBeacon { get; }
        public ConfigEntry<bool> RecordTraces { get; }
        public ConfigEntry<bool> VerboseLogging { get; }
        public ConfigEntry<bool> AllowGameVersionMismatch { get; }

        public NightshareConfig(ConfigFile file)
        {
            Mode = file.Bind("Session", "Mode", StartupMode.Off,
                "What to do on launch. Off means use the hotkeys. " +
                "Host and Join are for the automated launch scripts.");

            Transport = file.Bind("Session", "Transport", TransportChoice.Tcp,
                "Tcp works for two copies on one machine and over a LAN. " +
                "SteamP2P is not implemented yet.");

            Endpoint = file.Bind("Session", "Endpoint", "127.0.0.1:7777",
                "host:port to listen on or connect to. A bare port is fine when hosting.");

            PlayerName = file.Bind("Session", "PlayerName", "Player",
                "Name the other player sees.");

            // Key names come from UnityEngine.InputSystem.Key, not the legacy KeyCode,
            // because this game is built for the new Input System only and the legacy
            // UnityEngine.Input static throws on first use.
            //
            // Avoided on purpose:
            //   F12       Steam's screenshot key. Steam wins, so it looks broken.
            //   F10, F11  Lumen's overlay and scene report.
            const string KeyHelp = "Key name from UnityEngine.InputSystem.Key, for example F9, F7, Backquote.";

            // F9 was the direct Host key. The menu takes it over, because hosting is now
            // something you choose in the menu and this is a key already known to be free
            // of the game's own bindings, of Steam's and of Lumen's. Picking an untested
            // key for the one hotkey players actually need would be a poor trade.
            MenuKey = file.Bind("Hotkeys", "Menu", "F9",
                "Open and close the Nightshare menu, where a session is hosted or joined. " +
                "This is the only key most players need; the rest are testing shortcuts. " +
                KeyHelp);

            HostKey = file.Bind("Hotkeys", "Host", "",
                "Testing shortcut: start hosting without opening the menu. Empty by " +
                "default because the menu does this. " + KeyHelp);
            JoinKey = file.Bind("Hotkeys", "Join", "F7", $"Connect to Endpoint. {KeyHelp}");
            LeaveKey = file.Bind("Hotkeys", "Leave", "F6", $"Leave the current session. {KeyHelp}");
            StatusKey = file.Bind("Hotkeys", "Status", "F8", $"Log the session status. {KeyHelp}");

            SaveTestKey = file.Bind("Hotkeys", "SaveSelfTest", "F4",
                "Diagnostic. Serialises every manager's save packet and reads it back, " +
                "without applying anything. Proves the world snapshot plumbing. " + KeyHelp);

            ProbeKey = file.Bind("Hotkeys", "ProbeRig", "F5",
                "Diagnostic. Samples the player's transform hierarchy for six seconds and " +
                "reports which one carries the facing. Turn your character while it runs. " +
                KeyHelp);

            // ApplyWorldSnapshot was here. It gated the old manager-by-manager world
            // transfer, and by the end it gated nothing: a join sends the save file either
            // way. It is removed rather than left defaulting to false, because a setting
            // that no longer does anything is worse than no setting. It was still being
            // read as an explanation for test results it had no part in.

            // Raised from a guessed 1.5 after a normal walk animated as a full run.
            // The avatar logs the speeds it observes; tune these against those numbers.
            WalkSpeed = file.Bind("Avatars", "WalkSpeed", 3.0f,
                "Metres per second at which a remote player's walk animation reaches full " +
                "weight. Below this they are easing out of idle.");

            SprintSpeed = file.Bind("Avatars", "SprintSpeed", 6.0f,
                "Metres per second at which a remote player is fully running. Between " +
                "WalkSpeed and this, walk blends into run. Set it above the game's actual " +
                "sprint speed and a sprinting player never quite runs; set it too low and " +
                "an ordinary walk looks like a sprint.");

            ShowAvatarBeacon = file.Bind("Diagnostics", "ShowAvatarBeacon", true,
                "Put a cyan sphere over a remote player's head. The city is full of NPCs " +
                "and the avatar is a clone of one, so without this you cannot tell which " +
                "body is the other player. Replaced by nameplates later.");

            RecordTraces = file.Bind("Diagnostics", "RecordTraces", false,
                "Record every message to BepInEx/Nightshare-artifacts/*.ntrace. " +
                "Replays a desync in seconds instead of forty minutes of play.");

            VerboseLogging = file.Bind("Diagnostics", "VerboseLogging", true,
                "Log session events. Leave on until the mod is stable.");

            AllowGameVersionMismatch = file.Bind("Diagnostics", "AllowGameVersionMismatch", false,
                "DANGEROUS. Let peers on different game builds connect. A game patch moves " +
                "IL2CPP offsets and can change save layouts, so mismatched peers do not fail " +
                "cleanly: they connect, look fine, and corrupt each other's state. " +
                "Only ever set this to debug the version check itself.");
        }
    }
}
