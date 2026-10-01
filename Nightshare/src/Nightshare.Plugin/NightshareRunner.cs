using System;
using UnityEngine;

namespace Nightshare
{
    /// <summary>
    /// The mod's Unity shell. Drives <see cref="NightshareCore"/> and reads hotkeys, and
    /// deliberately does nothing else.
    /// <para>
    /// <b>Keep every method here free of Nightshare's own types.</b> Il2CppInterop registers
    /// an injected MonoBehaviour's methods into the IL2CPP domain and warns on any signature
    /// it cannot marshal, which previously produced a steady stream of
    /// <c>"has unsupported return type"</c> noise that would bury a real warning. Void and
    /// Unity types only.
    /// </para>
    /// </summary>
    public sealed class NightshareRunner : MonoBehaviour
    {
        /// <summary>Required so Il2CppInterop can construct this from the native side.</summary>
        public NightshareRunner(IntPtr ptr) : base(ptr) { }

        private bool _inputBroken;

        // Parsed once in Awake. Re-parsing a string every frame would be wasteful and
        // would re-log the same warning forever.
        private UnityEngine.InputSystem.Key _hostKey;
        private UnityEngine.InputSystem.Key _joinKey;
        private UnityEngine.InputSystem.Key _leaveKey;
        private UnityEngine.InputSystem.Key _statusKey;
        private UnityEngine.InputSystem.Key _probeKey;
        private UnityEngine.InputSystem.Key _saveTestKey;

        private void Awake()
        {
            var config = NightsharePlugin.Settings;
            _hostKey = ParseKey(config.HostKey.Value, "host");
            _joinKey = ParseKey(config.JoinKey.Value, "join");
            _leaveKey = ParseKey(config.LeaveKey.Value, "leave");
            _statusKey = ParseKey(config.StatusKey.Value, "status");
            _probeKey = ParseKey(config.ProbeKey.Value, "rig probe");
            _saveTestKey = ParseKey(config.SaveTestKey.Value, "save self test");

            NightshareCore.Instance.Initialise();
        }

        private void Update()
        {
            // UNSCALED, DELIBERATELY.
            //
            // Time.deltaTime is zero whenever the game sets Time.timeScale to zero, which is
            // what pausing does. Everything Nightshare drives from this must keep running
            // while the game is paused: the transport has to be pumped or the session dies,
            // and the host has to keep telling a guest that it is still paused. Driving the
            // clock broadcast from scaled time meant a paused host went silent, which is the
            // exact bug the pause flag exists to fix.
            //
            // In ordinary play the two are identical, so this costs nothing.
            var dt = Time.unscaledDeltaTime;

            // Before the core tick, and outside its world-loaded gate, because the whole
            // point of the trace is to watch a load that has not finished.
            Patches.LoadPathTrace.Tick(dt);

            NightshareCore.Instance.Tick(dt);
            HandleHotkeys();
        }

        private void OnDestroy() => NightshareCore.Instance.Shutdown();

        /// <summary>Called by the plugin on unload.</summary>
        public void LeaveSession() => NightshareCore.Instance.Leave();

        // ---------------------------------------------------------------- hotkeys

        /// <summary>
        /// Read hotkeys through the new Input System.
        /// <para>
        /// The legacy <c>UnityEngine.Input</c> static throws an Il2CppException in this
        /// game, confirmed at runtime: the project is built for the new Input System only.
        /// Do not be tempted back to <c>Input.GetKeyDown</c>, it cannot work here.
        /// </para>
        /// </summary>
        private void HandleHotkeys()
        {
            if (_inputBroken) return;

            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                if (keyboard == null) return;      // no keyboard yet, or headless

                if (WasPressed(keyboard, _hostKey)) NightshareCore.Instance.StartHosting();
                else if (WasPressed(keyboard, _joinKey)) NightshareCore.Instance.StartJoining();
                else if (WasPressed(keyboard, _leaveKey)) NightshareCore.Instance.Leave();
                else if (WasPressed(keyboard, _statusKey)) NightshareCore.Instance.LogStatus();
                else if (WasPressed(keyboard, _probeKey)) NightshareCore.Instance.ProbePlayerRig();
                else if (WasPressed(keyboard, _saveTestKey)) NightshareCore.Instance.RunSaveSelfTest();
            }
            catch (Exception ex)
            {
                // Say so once, then stop trying. The launch scripts set the role on the
                // command line and do not depend on hotkeys, so this is not fatal.
                _inputBroken = true;
                NightsharePlugin.Logger?.LogWarning(
                    $"Hotkeys unavailable ({ex.GetType().Name}: {ex.Message}). " +
                    $"Use the launch scripts or Session/Mode in the config instead.");
            }
        }

        private static bool WasPressed(UnityEngine.InputSystem.Keyboard keyboard,
                                       UnityEngine.InputSystem.Key key)
        {
            if (key == UnityEngine.InputSystem.Key.None) return false;

            var control = keyboard[key];
            return control != null && control.wasPressedThisFrame;
        }

        /// <summary>
        /// Parse a configured key name once. An unrecognised name disables that hotkey
        /// rather than the whole set, and says so.
        /// </summary>
        private static UnityEngine.InputSystem.Key ParseKey(string name, string what)
        {
            if (string.IsNullOrWhiteSpace(name)) return UnityEngine.InputSystem.Key.None;

            if (Enum.TryParse<UnityEngine.InputSystem.Key>(name, ignoreCase: true, out var key))
                return key;

            NightsharePlugin.Logger?.LogWarning(
                $"'{name}' is not a valid key name, so the {what} hotkey is disabled. " +
                $"Use a name from UnityEngine.InputSystem.Key, for example F9.");
            return UnityEngine.InputSystem.Key.None;
        }
    }
}
