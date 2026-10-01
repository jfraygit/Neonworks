using System;
using HarmonyLib;
using Nightshare.Replication;

namespace Nightshare.Patches
{
    /// <summary>
    /// Stops a player getting into bed until everybody is at one.
    /// <para>
    /// <b>Gate the interaction, not the clock.</b> The first attempt patched
    /// <c>TimeOfDayManager.Sleep</c>, which turns out to be the very last step of a long
    /// sequence. The result in play was that one player got the lay-down, the day summary,
    /// the shop rankings and the newspaper, and only the clock refused to move. The night
    /// has to be refused at the point the player interacts with the bed.
    /// </para>
    /// <para>
    /// <c>SleepManager.TryToSleep(IBed)</c> is that point. The prefix returns false to
    /// swallow the interaction while waiting, and the bed is remembered so the same call
    /// can be replayed for real once the last player is ready.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(Nivalis.SleepManager), nameof(Nivalis.SleepManager.TryToSleep))]
    internal static class SleepGatePatch
    {
        /// <summary>Returning false skips the original method.</summary>
        [HarmonyPrefix]
        private static bool Prefix(Nivalis.IBed bed)
        {
            try
            {
                // The agreed sleep, replayed once everybody is ready. Let it run.
                if (SleepCoordinator.Performing) return true;

                return NightshareCore.Instance.OnLocalSleepAttempt(bed);
            }
            catch (Exception ex)
            {
                // Never trap a player out of their own bed. If the gate faults, sleep.
                NightsharePlugin.Logger?.LogError($"Sleep gate failed, allowing the sleep: {ex}");
                return true;
            }
        }
    }
}
