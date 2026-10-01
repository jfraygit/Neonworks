using System;
using System.Collections.Generic;

namespace Nightshare.Replication
{
    /// <summary>Who owns a manager's state.</summary>
    internal enum Ownership
    {
        /// <summary>The host owns it. Sent to a joining client.</summary>
        World,

        /// <summary>Each player owns their own. Never sent; it travels home in their save.</summary>
        Player,

        /// <summary>Never sent to anybody.</summary>
        Excluded,

        /// <summary>Not yet classified. Treated as Excluded until decided.</summary>
        Undecided,
    }

    /// <summary>
    /// Which managers belong to the world and which belong to a player.
    /// <para>
    /// <b>The city and its story belong to the host. A guest is a second character living
    /// in it.</b> They bring their own money, inventory and skills, they can work in the
    /// host's businesses, and what they earn goes home with them. What they do not bring
    /// is a second version of the story.
    /// </para>
    /// <para>
    /// That is a deliberate change from an earlier design where each player kept their own
    /// quest flags. It does not survive contact with the game: Articy variables do not
    /// merely record progress, they drive the world. <c>.Owned</c>, <c>.IsHired</c>,
    /// <c>.Met</c>, <c>.Level</c> and <c>.MenuItems</c> decide who is standing where, which
    /// shops exist and who runs them. Two sets of those against one city means a guest
    /// holding a quest marker for something the city has already done.
    /// </para>
    /// <para>
    /// Classified by type NAME rather than by Type reference, because Il2Cpp Type identity
    /// is awkward to compare and names are what the logs and docs use. A manager that is
    /// not listed is Undecided and therefore not sent, which is the safe default: missing
    /// state is obviously absent, whereas sending the wrong state silently overwrites a
    /// player's own.
    /// </para>
    /// </summary>
    internal static class ManagerOwnership
    {
        private static readonly HashSet<string> WorldOwned = new(StringComparer.Ordinal)
        {
            // The city and its simulation.
            "TimeOfDayManager",
            "WeatherForecastController",
            "EconomyManager",
            "CurfewManager",
            "RentManager",
            "ApartmentManager",
            "NewsManager",
            "GreenhouseManager",
            "CollectibleManager",
            "FishingManager",
            "ScriptableObjectVariableDatabase",
            "TravelManager",

            // The NPC simulation. GhostSystem is this game's word for simulated agents,
            // not building placement previews.
            "PersonDataManager",
            "GhostManager",
            "GhostSystemInitializer",
            "AiOwnerManager",

            // Every container in the city. The player's own pockets live inside this under
            // the constant key PLAYER_INVENTORY and must be filtered out before sending;
            // see docs/recon.md.
            "InventoriesManager",

            // THE STORY. All of it belongs to the city, not to a visitor.
            //
            // The Articy variables are the reason. They are not a progress log, they are
            // what the world is made of: who has been met, who is hired, who owns what,
            // what is on a menu. A guest carrying their own copy would see a city that had
            // already moved past the quest they were being offered.
            "QuestManager",
            "ArticyGlobalVariablesLoader",
            "ArticyVariableManager",
            "ArticyTimerManager",
            "DialogueTreeProgressManager",
            "CharacterDialogueHistoryManager",
            "AgentsDialogueManager",

            // Which districts are reachable is a fact about this city.
            "TravelUnlockManager",
        };

        /// <summary>
        /// Per-guest state: who they are, what they own, what they know how to do.
        /// <para>
        /// <b>This does NOT travel home.</b> It is stored beside the host's save, one
        /// record per guest, and it stays in that city. A guest resumes the person they are
        /// in the host's world, and their own solo save is a different character entirely.
        /// </para>
        /// <para>
        /// A character carried between worlds would drift from both: two people who play
        /// alone between sessions end up at different points in different stories, and one
        /// of them has to lose progress to play together again. A character that lives in
        /// one world cannot drift from it. See docs/where-a-guest-lives.md.
        /// </para>
        /// </summary>
        private static readonly HashSet<string> PlayerOwned = new(StringComparer.Ordinal)
        {
            "PlayerManager",             // money, owned properties, receipts
            "SkillLevelController",      // skills earned
            "InspirationPointsManager",  // cooking inspiration
            "MealDatabase",              // recipes learned
            "AchievementManager",        // tied to the player's own Steam account
        };

        private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
        {
            // Telemetry, not game state. It was 76% of a real save file, and a joining
            // player has no use whatsoever for the host's analytics history.
            "TrackingSystem",

            // Input bindings are a local preference.
            "PlayerInputManager",
        };

        /// <summary>
        /// Not classified yet, and therefore not sent.
        /// <para>
        /// Empty. <c>AgentsDialogueManager</c> and <c>ArticyTimerManager</c> sat here while
        /// the story model was unsettled; once the story became the city's, both were
        /// plainly world state. Deciding the principle decided them.
        /// </para>
        /// </summary>
        private static readonly HashSet<string> Undecided = new(StringComparer.Ordinal);

        public static Ownership Classify(string managerTypeName)
        {
            if (string.IsNullOrEmpty(managerTypeName)) return Ownership.Undecided;

            if (WorldOwned.Contains(managerTypeName)) return Ownership.World;
            if (PlayerOwned.Contains(managerTypeName)) return Ownership.Player;
            if (Excluded.Contains(managerTypeName)) return Ownership.Excluded;
            if (Undecided.Contains(managerTypeName)) return Ownership.Undecided;

            return Ownership.Undecided;
        }

        /// <summary>True when this manager's state belongs in a world snapshot.</summary>
        public static bool IsWorldOwned(string managerTypeName) =>
            Classify(managerTypeName) == Ownership.World;

        /// <summary>
        /// Managers known to fail the serialiser round trip.
        /// <para>
        /// Empty, and worth keeping empty. <c>QuestManager</c> and
        /// <c>InventoriesManager</c> both lived here while they failed with what looked
        /// like two unrelated bugs. Both were one cause: the save version was hardcoded to
        /// 2 when the game runs at 151 and refuses anything below 146. At that version the
        /// game's own per-field gates skipped data the reader still expected, so the reader
        /// ran off the end. Fixing the version fixed both.
        /// </para>
        /// <para>
        /// If something lands here again, suspect a version or format mismatch before
        /// suspecting the individual packet.
        /// </para>
        /// </summary>
        public static readonly HashSet<string> KnownBroken = new(StringComparer.Ordinal);
    }
}
