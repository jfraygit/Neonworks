using System;
using System.IO;
using Nightshare.Core.Protocol;
using UnityEngine;

namespace Nightshare.Replication
{
    /// <summary>
    /// Moves the host's world to a guest by sending the save file itself.
    /// <para>
    /// The guest then loads it through the game's own load path, which is the whole point:
    /// zone, story, NPCs, UI and every system that reads the save all come up correct
    /// because the game is doing what it always does. The alternative, assembling the world
    /// out of manager packets and applying them to a running game, had to be discovered
    /// piece by piece and failed quietly in several places.
    /// </para>
    /// </summary>
    internal static class SaveTransfer
    {
        /// <summary>
        /// The slot the host writes when a guest joins.
        /// <para>
        /// A fixed, obviously-ours name. It is written on the host, where the player can
        /// see what it is, and never collides with a timestamped save of theirs.
        /// </para>
        /// </summary>
        public const string HostSlot = "nightshare_session";

        /// <summary>
        /// The slot a guest writes the host's world into.
        /// <para>
        /// <b>Never a guest's own save.</b> A guest's solo game is not ours to touch, so
        /// the incoming world goes to a slot that belongs to the mod and nothing else.
        /// </para>
        /// </summary>
        public const string GuestSlot = "nightshare_visiting";

        private static string PathFor(string saveName)
        {
            try { return Path.Combine(Application.persistentDataPath, saveName + ".sav"); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Active scene and local player position, as one line for the log.
        /// <para>
        /// Logged on the host at capture and on the guest once the load settles, so the two
        /// can be compared directly. A guest arriving somewhere unexpected is otherwise only
        /// describable in prose, which is not something a log can answer.
        /// </para>
        /// </summary>
        public static string DescribeLocation()
        {
            var scene = "?";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
            catch (Exception) { }

            try
            {
                var manager = UnityEngine.Object.FindObjectOfType<Nivalis.PlayerManager>();
                var go = manager?.LocalPlayer?.PlayerGameObject;
                if (go != null)
                {
                    var p = go.transform.position;
                    return $"scene '{scene}' at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0})";
                }
            }
            catch (Exception) { }

            return $"scene '{scene}', no local player yet";
        }

        // ---------------------------------------------------------------- host

        /// <summary>
        /// Save, read the file back, and hand the bytes over. Returns null on failure,
        /// having logged why.
        /// </summary>
        public static SaveTransferV1 TryCapture(Action<string> log)
        {
            Nivalis.SerializationManager manager;
            try
            {
                manager = UnityEngine.Object.FindObjectOfType<Nivalis.SerializationManager>();
            }
            catch (Exception ex)
            {
                log($"Save: could not reach SerializationManager: {ex.Message}");
                return null;
            }

            if (manager == null)
            {
                log("Save: no SerializationManager, is a world loaded?");
                return null;
            }

            try
            {
                // isAutoSave false: this is a real save the player can see and reload.
                if (!manager.Save(HostSlot, isAutoSave: false))
                {
                    log($"Save: the game refused to save to '{HostSlot}'.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                log($"Save: saving threw: {ex.Message}");
                return null;
            }

            var path = PathFor(HostSlot);
            if (path == null || !File.Exists(path))
            {
                log($"Save: saved, but no file appeared at '{path}'.");
                return null;
            }

            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                log($"Save: could not read the file back: {ex.Message}");
                return null;
            }

            var day = 0;
            try { day = Nivalis.TimeOfDayManager.GameplayGameDay; } catch (Exception) { }

            log($"Save: captured {data.Length:N0} bytes ({data.Length / 1024:N0} KB), day {day}");

            // Where the host actually is at the moment of capture. A guest loading this file
            // should come up in the same place, so when they do not, these two lines are the
            // difference between "the save is wrong" and "the load put them somewhere else"
            // without having to guess which.
            log($"Save: host is at {DescribeLocation()}");

            return new SaveTransferV1
            {
                SaveName = GuestSlot,
                GameplayGameDay = day,
                Data = data,
            };
        }

        // ---------------------------------------------------------------- guest

        /// <summary>
        /// Write the host's world down and load it. Returns false on failure, having
        /// logged why; the guest then stays in its own world rather than a broken one.
        /// </summary>
        public static bool TryApply(SaveTransferV1 msg, Action<string> log)
        {
            if (msg?.Data == null || msg.Data.Length == 0)
            {
                log("Save: the host sent nothing.");
                return false;
            }

            // Ignore whatever name the host suggested and use our own constant. A slot name
            // off the wire is a path, and a path off the wire is how a mod overwrites
            // something it had no business touching.
            var path = PathFor(GuestSlot);
            if (path == null)
            {
                log("Save: could not work out where to write.");
                return false;
            }

            try
            {
                File.WriteAllBytes(path, msg.Data);
                log($"Save: wrote {msg.Data.Length:N0} bytes to '{GuestSlot}'");
            }
            catch (Exception ex)
            {
                log($"Save: could not write the file: {ex.Message}");
                return false;
            }

            Nivalis.SerializationManager manager;
            try
            {
                manager = UnityEngine.Object.FindObjectOfType<Nivalis.SerializationManager>();
            }
            catch (Exception ex)
            {
                log($"Save: could not reach SerializationManager: {ex.Message}");
                return false;
            }

            if (manager == null)
            {
                log("Save: no SerializationManager to load with.");
                return false;
            }

            try
            {
                if (!manager.DoesSaveExist(GuestSlot))
                {
                    log($"Save: the game does not recognise '{GuestSlot}' as a save.");
                    return false;
                }

                log($"Save: loading the host's world (day {msg.GameplayGameDay})");
                manager.Load(GuestSlot);
                return true;
            }
            catch (Exception ex)
            {
                log($"Save: loading threw: {ex.Message}");
                return false;
            }
        }
    }
}
