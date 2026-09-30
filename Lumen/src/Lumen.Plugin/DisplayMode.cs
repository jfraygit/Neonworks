using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Nivalis.UI.Concrete;
using UnityEngine;

namespace Lumen
{
    /// <summary>
    /// Direct control of the game's window: strip the caption and frame styles, make it a
    /// popup, and place it over the monitor it is already on.
    /// </summary>
    internal static class WindowStyle
    {
        private const int GwlStyle = -16;

        private const uint WsPopup = 0x80000000;
        private const uint WsVisible = 0x10000000;
        private const uint WsCaption = 0x00C00000;
        private const uint WsThickFrame = 0x00040000;
        private const uint WsMinimizeBox = 0x00020000;
        private const uint WsMaximizeBox = 0x00010000;
        private const uint WsSysMenu = 0x00080000;
        private const uint WsBorder = 0x00800000;
        private const uint WsDlgFrame = 0x00400000;

        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpNoOwnerZOrder = 0x0200;

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowLongA(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SetWindowLongA(IntPtr hWnd, int nIndex, uint dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        private const uint MonitorDefaultToPrimary = 1;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private static IntPtr _window;
        private static int _restyleCount;

        /// <summary>The window handle, cached once the game has focus. Zero until then.</summary>
        internal static IntPtr Handle
        {
            get
            {
                if (_window != IntPtr.Zero) return _window;

                try
                {
                    // Only valid while the game owns focus, which is why this is cached
                    // rather than read on demand.
                    var active = GetActiveWindow();
                    if (active != IntPtr.Zero) _window = active;
                }
                catch (Exception) { }

                return _window;
            }
        }

        internal static string DescribeStyle()
        {
            var hwnd = Handle;
            if (hwnd == IntPtr.Zero) return "no window yet";

            try
            {
                uint style = GetWindowLongA(hwnd, GwlStyle);
                bool bordered = (style & (WsCaption | WsThickFrame | WsBorder | WsDlgFrame)) != 0;
                return $"0x{style:X8} {(bordered ? "BORDERED" : "borderless")}";
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>
        /// Strips the frame and covers exactly the monitor the window is already on.
        /// <para>
        /// The rectangle comes from Windows rather than from Unity's screen size, because
        /// 0,0 is the corner of the whole virtual desktop rather than of the display the
        /// game is on. They only coincide on a single-monitor machine.
        /// </para>
        /// </summary>
        internal static bool MakeBorderless()
        {
            var hwnd = Handle;
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                uint style = GetWindowLongA(hwnd, GwlStyle);
                uint wanted = (style & ~(WsCaption | WsThickFrame | WsMinimizeBox |
                                         WsMaximizeBox | WsSysMenu | WsBorder | WsDlgFrame))
                              | WsPopup | WsVisible;

                if (!TryGetMonitorBounds(hwnd, out int x, out int y, out int width, out int height))
                    return false;

                bool styleWrong = style != wanted;
                bool placementWrong = !IsAlreadyPlaced(hwnd, x, y, width, height);

                if (!styleWrong && !placementWrong) return false;

                // The first few only: enough to show a mode being repeatedly put back,
                // without filling the log with identical lines.
                if (_restyleCount < 4)
                {
                    LumenPlugin.Log.LogInfo(
                        $"Window {(styleWrong ? "style" : "placement")} corrected: " +
                        $"0x{style:X8} -> 0x{wanted:X8} at {x},{y} {width}x{height}.");
                }

                _restyleCount++;

                if (styleWrong) SetWindowLongA(hwnd, GwlStyle, wanted);

                SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
                    SwpFrameChanged | SwpShowWindow | SwpNoOwnerZOrder);

                return true;
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogWarning($"Could not restyle the window: {ex.Message}");
                return false;
            }
        }

        private static bool TryGetMonitorBounds(IntPtr hwnd, out int x, out int y,
            out int width, out int height)
        {
            x = y = width = height = 0;

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero) return false;

            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            // Full monitor rect, not the work area: the taskbar should be covered.
            x = info.Monitor.Left;
            y = info.Monitor.Top;
            width = info.Monitor.Right - info.Monitor.Left;
            height = info.Monitor.Bottom - info.Monitor.Top;

            return width > 0 && height > 0;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        private static bool IsAlreadyPlaced(IntPtr hwnd, int x, int y, int width, int height)
        {
            try
            {
                if (!GetWindowRect(hwnd, out var rect)) return false;
                return rect.Left == x && rect.Top == y &&
                       rect.Right - rect.Left == width && rect.Bottom - rect.Top == height;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Forces borderless fullscreen window.
    /// <para>
    /// The game's own graphics menu only offers a Fullscreen on/off toggle, and "on" gives
    /// exclusive fullscreen. Borderless keeps alt-tabbing instant and lets the overlay and
    /// other windows sit on top, which matters while measuring.
    /// </para>
    /// <para>
    /// It has to be re-asserted rather than set once: the game applies its own display mode
    /// whenever graphics settings are applied, and the half-resolution probe changes the
    /// backbuffer too. So this re-checks on a slow timer and only acts when the mode has
    /// actually drifted, with a cooldown so it can never fight the game into flickering.
    /// </para>
    /// </summary>
    internal static class DisplayMode
    {
        // Windowed, not FullScreenWindow. Screen.fullScreenMode reports the mode that was
        // requested rather than the one in effect, and FullScreenWindow can still leave an
        // exclusive swapchain in place - which minimises the game on focus loss. Windowed
        // cannot, and the borderless look comes from the window styles instead.
        private const FullScreenMode Wanted = FullScreenMode.Windowed;

        private const float CheckInterval = 1.5f;
        private const float CooldownAfterForcing = 3f;

        private static float _timer;
        private static int _forcedCount;

        /// <summary>Current mode and size, for the overlay. Cheap enough to read per frame.</summary>
        internal static string Describe()
        {
            try
            {
                return $"{Screen.fullScreenMode} {Screen.width}x{Screen.height}  " +
                       $"style {WindowStyle.DescribeStyle()}";
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>
        /// Hooks the game's own display-mode application so the borderless mode is
        /// re-asserted right after the game sets its own, rather than racing it on a timer.
        /// </summary>
        internal static void Install(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(GraphicsSettingsUI), "SetFullScreenMode");
                if (target == null)
                {
                    LumenPlugin.Log.LogWarning(
                        "GraphicsSettingsUI.SetFullScreenMode not found; borderless falls back " +
                        "to the timer, which the game may override. Re-dump after a game patch.");
                    return;
                }

                harmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(DisplayMode), nameof(AfterGameSetFullScreenMode)));

                LumenPlugin.Log.LogInfo("Display mode hook installed.");
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Could not hook the display mode: {ex}");
            }
        }

        private static void AfterGameSetFullScreenMode()
        {
            // Never throw into IL2CPP's native call stack.
            try
            {
                if (!LumenConfig.BorderlessWindow.Value) return;

                // Act next tick rather than inside the game's own call: changing the mode
                // from within the method that just set it is a good way to find a re-entry
                // bug in someone else's code.
                _timer = 0f;
            }
            catch (Exception) { }
        }

        internal static void Tick(float unscaledDeltaTime)
        {
            if (!LumenConfig.BorderlessWindow.Value) return;

            _timer -= unscaledDeltaTime;
            if (_timer > 0f) return;

            _timer = CheckInterval;

            try
            {
                int screenWidth = Display.main != null ? Display.main.systemWidth : Screen.width;
                int screenHeight = Display.main != null ? Display.main.systemHeight : Screen.height;

                // Mode first, styling second: entering windowed mode puts the frame back,
                // so restyling before the mode change would be undone immediately.
                if (Screen.fullScreenMode == Wanted)
                {
                    WindowStyle.MakeBorderless();
                    return;
                }

                var was = Screen.fullScreenMode;

                // Keep the current backbuffer size; only the mode is ours to decide. Using
                // the explicit overload rather than assigning fullScreenMode because the
                // half-resolution probe also drives SetResolution and the two need to agree.
                // One pixel short of the desktop height on purpose: a windowed backbuffer
                // exactly the size of the desktop gets promoted back to fullscreen by Unity.
                // SetWindowPos below still covers the whole monitor, so nothing is cropped.
                Screen.SetResolution(screenWidth, screenHeight - 1, Wanted);
                WindowStyle.MakeBorderless();

                // Give the change time to land before looking again, or a mode the game
                // re-asserts every frame would turn into a fight.
                _timer = CooldownAfterForcing;
                _forcedCount++;

                // Log the first few attempts only, but enough of them that a mode being
                // repeatedly put back is visible rather than silent.
                if (_forcedCount <= 6)
                {
                    LumenPlugin.Log.LogInfo(
                        $"Display mode -> windowed+borderless #{_forcedCount} " +
                        $"(was {was}, {screenWidth}x{screenHeight}).");

                    if (_forcedCount == 6)
                        LumenPlugin.Log.LogWarning(
                            "Borderless has been re-forced six times - something is putting " +
                            "the mode back. Further attempts will not be logged.");
                }
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogWarning($"Could not set borderless window: {ex.Message}");

                // Do not retry in a tight loop if the platform refuses.
                _timer = 30f;
            }
        }
    }
}
