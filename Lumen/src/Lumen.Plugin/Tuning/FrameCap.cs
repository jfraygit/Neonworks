using System;
using UnityEngine;

namespace Lumen.Tuning
{
    /// <summary>
    /// Holds the frame rate down so a problem that only appears at low frame rates can be
    /// reproduced on a machine fast enough not to have it.
    /// <para>
    /// The game's own <c>TargetFrameRate</c> in settings.ini is not honoured for arbitrary
    /// values: anything outside the list its menu offers is read and discarded, so it cannot
    /// be used for this.
    /// </para>
    /// <para>
    /// Re-applied on a timer rather than set once, for the same reason the borderless window
    /// is. Applying graphics settings resets display state, and a cap that lapses halfway
    /// through a test is worse than no cap at all, because the test still looks like it ran.
    /// </para>
    /// </summary>
    internal static class FrameCap
    {
        private const float ReassertInterval = 1f;

        private static float _sinceReassert;
        private static int _applied = int.MinValue;
        private static bool _restored = true;

        internal static void Tick(float unscaledDeltaTime)
        {
            int wanted = LumenConfig.DevFrameCap.Value;

            // Off, and nothing of ours left behind to undo.
            if (wanted <= 0)
            {
                if (!_restored) Restore();
                return;
            }

            _sinceReassert += unscaledDeltaTime;

            bool changed = wanted != _applied;
            if (!changed && _sinceReassert < ReassertInterval) return;

            _sinceReassert = 0f;

            try
            {
                // vSyncCount overrides targetFrameRate entirely, so a cap means nothing
                // while it is on. Turning it off is part of applying the cap.
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;

                if (Application.targetFrameRate != wanted)
                    Application.targetFrameRate = wanted;

                if (changed)
                {
                    LumenPlugin.Log.LogWarning(
                        $"Dev frame cap ON at {wanted} fps. This is a testing aid, set " +
                        "DevFrameCap back to 0 when finished.");
                }

                _applied = wanted;
                _restored = false;
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogWarning($"Frame cap failed: {ex.Message}");
            }
        }

        /// <summary>Hands the frame rate back to the game.</summary>
        internal static void Restore()
        {
            try
            {
                Application.targetFrameRate = -1;
                LumenPlugin.Log.LogInfo("Dev frame cap released.");
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogWarning($"Frame cap restore failed: {ex.Message}");
            }

            _applied = int.MinValue;
            _restored = true;
        }
    }
}
