using System;
using System.Runtime.InteropServices;

namespace Lumen
{
    /// <summary>
    /// Keyboard edge detection straight from Win32.
    /// <para>
    /// The game never calls legacy <c>UnityEngine.Input</c> anywhere in the dump; it uses
    /// the Input System package exclusively, which means legacy Input is very likely to
    /// throw on access. Registering our own actions with the Input System is worse: action
    /// maps are process-wide, so a debug overlay would be reaching into the thing the game
    /// depends on for movement.
    /// </para>
    /// <para>
    /// <c>GetAsyncKeyState</c> sidesteps both problems. It ignores window focus, so every
    /// read is gated on <see cref="UnityEngine.Application.isFocused"/> — otherwise typing
    /// in another window would drive the overlay.
    /// </para>
    /// </summary>
    internal static class Hotkeys
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // Virtual key codes. Named rather than magic so the overlay reads cleanly.
        internal const int VkBack = 0x08;
        internal const int VkReturn = 0x0D;
        internal const int VkLeft = 0x25;
        internal const int VkUp = 0x26;
        internal const int VkRight = 0x27;
        internal const int VkDown = 0x28;
        internal const int VkF9 = 0x78;
        internal const int VkF10 = 0x79;
        internal const int VkF11 = 0x7A;
        internal const int VkF12 = 0x7B;

        private static readonly bool[] WasDown = new bool[256];

        /// <summary>
        /// True on the frame the key transitions from up to down. Call
        /// <see cref="Poll"/> exactly once per frame before any of these.
        /// </summary>
        internal static bool Pressed(int vKey) => _pressed[vKey];

        private static readonly bool[] _pressed = new bool[256];

        private static readonly int[] Watched =
        {
            VkBack, VkReturn, VkLeft, VkUp, VkRight, VkDown, VkF9, VkF10, VkF11, VkF12
        };

        internal static void Poll(bool windowFocused)
        {
            for (int i = 0; i < Watched.Length; i++)
            {
                int vk = Watched[i];

                if (!windowFocused)
                {
                    // Drop the latch too, so alt-tabbing back in does not fire a phantom press.
                    _pressed[vk] = false;
                    WasDown[vk] = false;
                    continue;
                }

                bool down;
                try
                {
                    down = (GetAsyncKeyState(vk) & 0x8000) != 0;
                }
                catch (Exception)
                {
                    down = false;
                }

                _pressed[vk] = down && !WasDown[vk];
                WasDown[vk] = down;
            }
        }
    }
}
