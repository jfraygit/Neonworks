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

            var where = $"scene '{scene}'";

            try
            {
                var manager = UnityEngine.Object.FindObjectOfType<Nivalis.PlayerManager>();
                var go = manager?.LocalPlayer?.PlayerGameObject;
                if (go != null)
                {
                    var p = go.transform.position;
                    where += $" at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0})";
                }
                else
                {
                    where += ", no local player yet";
                }
            }
            catch (Exception) { }

            return where + DescribeInterior();
        }

        /// <summary>
        /// Whether the player is inside an apartment, and whether it is one the save's
        /// player actually owns.
        /// <para>
        /// <b>Coordinates cannot answer this and it was a mistake to think they could.</b>
        /// A host and a guest were measured 1.2 m apart and called identical, when they were
        /// in fact standing in two different apartments. Interiors sit at similar local
        /// positions, so position says where but never which. <c>OwnedEntered</c> is the
        /// game's own answer: entering someone else's apartment and entering your own are
        /// different flags, and that is the distinction the measurement needs.
        /// </para>
        /// </summary>
        private static string DescribeInterior()
        {
            try
            {
                // Nivalis.Apartment is a NAMESPACE, not a class. Same shape as
                // Nivalis.InventorySystem; guessing Nivalis.ApartmentManager costs a build.
                var apartments = Nivalis.Apartment.ApartmentManager.Instance;
                if (apartments == null) return "";

                if (!apartments.ApartmentEntered && !apartments.ShelterEntered) return ", outdoors";

                var kind = apartments.OwnedEntered ? "OWN apartment" : "SOMEONE ELSE'S apartment";
                if (apartments.ShelterEntered && !apartments.ApartmentEntered) kind = "a shelter";

                return $", inside {kind}";
            }
            catch (Exception) { return ""; }
        }

        // ---------------------------------------------------------------- host

        /// <summary>
        /// Save, read the file back, and hand the bytes over. Returns null on failure,
        /// having logged why.
        /// </summary>
        public static SaveTransferV2 TryCapture(Action<string> log)
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
            log($"Save: host is at {DescribeLocation()}");

            var seconds = 0;
            try { seconds = Nivalis.TimeOfDayManager.TotalGameSeconds; } catch (Exception) { }

            var message = new SaveTransferV2
            {
                SaveName = GuestSlot,
                GameplayGameDay = day,
                TotalGameSeconds = seconds,
                Data = data,
            };

            // The same instant as the game itself describes it. Built from the game's own
            // TimeStamp rather than worked out from the second count, so our idea of which
            // day it is cannot disagree with the game's.
            try
            {
                var stamp = new Nivalis.SerializableTimeStamp(Nivalis.TimeOfDayManager.CurrentTime);
                message.GameDay = stamp.GameDay;
                message.Hour = stamp.Hour;
                message.Minute = stamp.Minute;
                message.Second = stamp.Second;

                // Whether SerializableTimeStamp.GameDay is the same number as
                // TimeOfDayManager.GameplayGameDay is an assumption the in-session resync
                // relies on, and the two are not obviously the same thing. Logged once so
                // it is a measured fact rather than a hope.
                if (stamp.GameDay != day)
                {
                    log($"Save: NOTE the time stamp's GameDay is {stamp.GameDay} but " +
                        $"GameplayGameDay is {day}. They are not interchangeable.");
                }
            }
            catch (Exception ex)
            {
                message.GameDay = -1;   // tells the guest it cannot set the clock absolutely
                log($"Save: could not read the host's time stamp ({ex.Message}).");
            }

            // The arrival point. Without it a guest lands at the zone's own arrival spot,
            // which in testing was a stranger's apartment two floors up.
            try
            {
                var player = UnityEngine.Object.FindObjectOfType<Nivalis.PlayerManager>()
                                              ?.LocalPlayer?.PlayerGameObject;
                if (player != null)
                {
                    var t = player.transform;
                    message.X = t.position.x;
                    message.Y = t.position.y;
                    message.Z = t.position.z;
                    message.Yaw = t.eulerAngles.y;
                    message.HasArrivalPoint = true;
                }
                else
                {
                    log("Save: could not find the host's player, so the guest will arrive " +
                        "wherever the game puts them.");
                }
            }
            catch (Exception ex)
            {
                log($"Save: could not read the host's position ({ex.Message}).");
            }

            // Which apartment the host is inside, if any. A one-off sweep on join, not a
            // per-frame one; see the scene-sweep rule in CLAUDE.md.
            try
            {
                var entered = FindEnteredApartment();
                if (entered != null)
                {
                    message.ApartmentGuid = entered.Apartment?.Guid ?? "";
                    log($"Save: the host is inside apartment {message.ApartmentGuid}");
                }
            }
            catch (Exception ex)
            {
                log($"Save: could not tell which apartment the host is in ({ex.Message}).");
            }

            return message;
        }

        /// <summary>The apartment the local player is standing in, or null if outdoors.</summary>
        private static Nivalis.Apartment.ApartmentController FindEnteredApartment()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Nivalis.Apartment.ApartmentController>();
                if (all == null) return null;

                foreach (var controller in all)
                {
                    if (controller != null && controller.Entered) return controller;
                }
            }
            catch (Exception) { }

            return null;
        }

        // ---------------------------------------------------------------- guest

        /// <summary>
        /// Write the host's world down and load it. Returns false on failure, having
        /// logged why; the guest then stays in its own world rather than a broken one.
        /// </summary>
        /// <summary>
        /// Put the guest where the host is, after the load has finished placing them
        /// somewhere else.
        /// <para>
        /// Offset sideways by a couple of metres so two people do not occupy one spot.
        /// Returns false if there was nothing to apply or nobody to move.
        /// </para>
        /// </summary>
        public static bool TryPlaceAtArrival(SaveTransferV2 msg, Action<string> log)
        {
            if (msg == null || !msg.HasArrivalPoint) return false;

            try
            {
                var player = UnityEngine.Object.FindObjectOfType<Nivalis.PlayerManager>()
                                              ?.LocalPlayer?.PlayerGameObject;
                if (player == null)
                {
                    log("Arrival: no local player to place.");
                    return false;
                }

                var target = new Vector3(msg.X, msg.Y, msg.Z);

                // Two metres to the host's right, so nobody spawns inside anybody.
                var facing = Quaternion.Euler(0f, msg.Yaw, 0f);
                target += facing * Vector3.right * 2f;

                var before = player.transform.position;

                // A CharacterController overrides transform writes while it is enabled, so
                // it has to be switched off around the move or the position silently does
                // not take. Re-enabled immediately; leaving it off would strand the player.
                var controller = player.GetComponent<CharacterController>();
                var hadController = controller != null && controller.enabled;

                if (hadController) controller.enabled = false;
                player.transform.position = target;
                player.transform.rotation = facing;
                if (hadController) controller.enabled = true;

                log($"Arrival: moved from ({before.x:0.0}, {before.y:0.0}, {before.z:0.0}) " +
                    $"to ({target.x:0.0}, {target.y:0.0}, {target.z:0.0}) beside the host");

                EnterHostsApartment(msg.ApartmentGuid, log);
                return true;
            }
            catch (Exception ex)
            {
                log($"Arrival: could not place the guest ({ex.Message}). " +
                    $"They stay where the game put them.");
                return false;
            }
        }

        /// <summary>
        /// Switch the apartment the guest now stands in from its exterior to its interior,
        /// the way walking through the door would have.
        /// <para>
        /// <b>Teleporting into a building is not the same as entering it.</b> An apartment
        /// keeps two sets of objects and <c>Enter</c> swaps which is active. Arriving by
        /// teleport leaves the exterior set up, which in play is a sealed box: the host is
        /// visible and moving, the exit door is visible as a silhouette, and nothing else is
        /// there. Walking out through that door fixed it, because exiting runs the half of
        /// the swap that entering never did.
        /// </para>
        /// </summary>
        private static void EnterHostsApartment(string guid, Action<string> log)
        {
            if (string.IsNullOrEmpty(guid)) return;   // the host was outdoors

            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Nivalis.Apartment.ApartmentController>();
                if (all == null)
                {
                    log($"Arrival: no apartments in this scene, cannot enter {guid}.");
                    return;
                }

                foreach (var controller in all)
                {
                    if (controller?.Apartment == null) continue;
                    if (controller.Apartment.Guid != guid) continue;

                    if (controller.Entered)
                    {
                        log("Arrival: already inside the host's apartment.");
                        return;
                    }

                    controller.Enter();
                    log($"Arrival: entered the host's apartment ({guid})");
                    return;
                }

                log($"Arrival: the host's apartment ({guid}) is not in this scene. " +
                    $"Staying put rather than entering the wrong one.");
            }
            catch (Exception ex)
            {
                log($"Arrival: could not enter the host's apartment ({ex.Message}).");
            }
        }

        public static bool TryApply(SaveTransferV2 msg, Action<string> log)
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
