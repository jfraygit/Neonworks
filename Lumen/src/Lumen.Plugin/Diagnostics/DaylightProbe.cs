using System;
using System.Reflection;
using HarmonyLib;
using Nivalis;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// Jumps the clock to midday, for judging anything shadow-related.
    /// <para>
    /// Only the directional light casts shadows, and at night that is the moon - so a
    /// shadow comparison made after dark says nothing about how the game looks at noon.
    /// </para>
    /// <para>
    /// Uses the game's own <c>Dev_SetTime</c> so the whole lighting cycle moves together,
    /// and puts the original time back on restore.
    /// </para>
    /// </summary>
    internal sealed class DaylightProbe : Probe
    {
        internal override string Name => "Jump to midday (shadow test)";
        internal override string Hint =>
            "Diagnostic. Moves the world clock, so restore it before you save. Turn this on, " +
            "then A/B the shadow proxies and judge the shadows in sunlight.";

        private const float Midday = 0.5f;

        private float _originalFraction = -1f;
        private MethodInfo _setTime;

        private bool Resolve()
        {
            if (_setTime != null) return true;

            _setTime = AccessTools.Method(typeof(TimeOfDayManager), "Dev_SetTime",
                new[] { typeof(float), typeof(bool) });

            if (_setTime == null)
            {
                LumenPlugin.Log.LogWarning(
                    "TimeOfDayManager.Dev_SetTime(float, bool) not found. Re-dump after a game patch.");
                Status = "unavailable";
            }

            return _setTime != null;
        }

        private static TimeOfDayManager Instance()
        {
            // Going through Find rather than Singleton<T>.Instance: resolving the generic
            // singleton base through interop is fiddly and this runs once per toggle.
            var found = Find.All<TimeOfDayManager>();
            return found.Count > 0 ? found[0] : null;
        }

        protected override void Apply()
        {
            if (!Resolve()) return;

            var manager = Instance();
            if (manager == null)
            {
                Status = "no TimeOfDayManager";
                LumenPlugin.Log.LogWarning($"{Name}: no TimeOfDayManager in the scene.");
                return;
            }

            // ClockHourFloat is hours past midnight; Dev_SetTime wants a fraction of a day.
            _originalFraction = TimeOfDayManager.ClockHourFloat / 24f;

            _setTime.Invoke(manager, new object[] { Midday, true });

            Status = $"midday (was {TimeOfDayManager.ClockHourFloat:0.0}h)";
            LumenPlugin.Log.LogInfo($"{Name}: clock {_originalFraction * 24f:0.00}h -> 12.00h, paused.");
        }

        protected override void Restore()
        {
            if (_setTime == null || _originalFraction < 0f) return;

            var manager = Instance();
            if (manager == null) return;

            _setTime.Invoke(manager, new object[] { _originalFraction, false });

            LumenPlugin.Log.LogInfo($"{Name}: clock restored to {_originalFraction * 24f:0.00}h, unpaused.");

            _originalFraction = -1f;
            Status = null;
        }
    }
}
