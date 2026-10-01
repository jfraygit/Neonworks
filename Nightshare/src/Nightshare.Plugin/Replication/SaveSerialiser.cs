using System;
using System.Collections.Generic;
using System.Text;
using Nivalis;

namespace Nightshare.Replication
{
    /// <summary>
    /// Turns a manager's <c>ISavePacket</c> into bytes and back, using the game's own
    /// serialiser rather than a parallel one.
    /// <para>
    /// <b>Why reuse the game's serialiser.</b> Every manager already implements
    /// <c>Save(ISaveWriter)</c> and <c>Load(SaveReader)</c>, with per-field versioning
    /// built in via firstVersion and lastVersion. Writing our own would mean a second
    /// definition of every packet's layout, and the moment the two drifted we would have
    /// a desync whose cause was our own serialiser. Same code, same bytes, no drift.
    /// </para>
    /// <para>
    /// <b>No temp files.</b> Easy Save 2 has a Memory save location, so a packet
    /// round-trips entirely in RAM: ES2Settings(Memory) to an ES2Writer, wrapped in the
    /// game's BinarySaveWriter. Reading back uses ES2Reader.Create(byte[], settings).
    /// </para>
    /// </summary>
    internal static class SaveSerialiser
    {
        /// <summary>
        /// Fallback if the game's configuration cannot be read. Taken from the version int
        /// in a real save file header, which turned out to be the file format version
        /// rather than the save version.
        /// </summary>
        private const int FallbackSaveVersion = 2;

        private static int _saveVersion = -1;

        /// <summary>
        /// The version handed to BinarySaveWriter and SaveReader, driving the game's own
        /// per-field version gates.
        /// <para>
        /// <b>Read from the game rather than hardcoded.</b> Symmetric read and write should
        /// in principle work at any version, but the developers only ever exercise the
        /// current one, so a Save/Load asymmetry in a field gate at an old version is
        /// nobody's bug but ours to trip over. Using the version the game itself runs at is
        /// the only one guaranteed self-consistent.
        /// </para>
        /// </summary>
        public static int SaveVersion
        {
            get
            {
                if (_saveVersion > 0) return _saveVersion;

                try
                {
                    var config = SerializationManagerConfiguration.Instance;
                    if (config != null && config.LatestSaveVersion > 0)
                    {
                        _saveVersion = config.LatestSaveVersion;
                        NightsharePlugin.Logger?.LogInfo(
                            $"Save version is {_saveVersion} (game reported), " +
                            $"minimum supported {config.MinimalSupportedSaveVersion}");
                        return _saveVersion;
                    }
                }
                catch (Exception ex)
                {
                    NightsharePlugin.Logger?.LogWarning(
                        $"Could not read the save version from the game ({ex.Message}), " +
                        $"falling back to {FallbackSaveVersion}");
                }

                _saveVersion = FallbackSaveVersion;
                return _saveVersion;
            }
        }

        /// <summary>
        /// Serialise a packet. Returns null on failure, having logged why.
        /// </summary>
        public static byte[] TrySerialise(ISavePacket packet, out string error)
        {
            error = null;

            if (packet == null) { error = "packet was null"; return null; }

            ES2Writer writer = null;
            try
            {
                var settings = new ES2Settings(ES2Settings.SaveLocation.Memory);
                settings.encrypt = false;   // saves are plaintext; encrypting the wire buys nothing here

                writer = ES2Writer.Create(settings);
                if (writer == null) { error = "ES2Writer.Create returned null"; return null; }

                var saveWriter = new BinarySaveWriter(SaveVersion, writer);


                // Two Il2CppInterop casts, both required rather than stylistic:
                //
                //   packet -> ISaveable    Save and Load are declared on ISaveable, which
                //                          ISavePacket extends. Interop does not surface
                //                          base interface members through a derived one.
                //
                //   writer -> ISaveWriter  A concrete Il2Cpp class does not implicitly
                //                          convert to an Il2Cpp interface either.
                packet.Cast<ISaveable>().Save(saveWriter.Cast<ISaveWriter>());

                // Flush the BinaryWriter explicitly before asking ES2 to store.
                // BinaryWriter buffers, and a large packet can otherwise be stored with
                // its tail still sitting in the buffer. That presents as the reader
                // hitting end of stream part way through a payload that looked complete.
                try { writer.writer?.Flush(); } catch (Exception) { }

                try { writer.Save(); } catch (Exception) { }

                try { writer.writer?.Flush(); } catch (Exception) { }

                var bytes = writer.stream?.ReadAllBytes();
                if (bytes == null) { error = "the memory stream produced no bytes"; return null; }

                return bytes;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
            finally
            {
                try { writer?.Dispose(); } catch (Exception) { }
            }
        }

        /// <summary>
        /// Load bytes into an existing packet. The packet is mutated in place; the caller
        /// decides whether to hand it to a manager afterwards.
        /// </summary>
        public static bool TryDeserialise(ISavePacket packet, byte[] bytes, out string error)
        {
            error = null;

            if (packet == null) { error = "packet was null"; return false; }
            if (bytes == null) { error = "no bytes"; return false; }

            // An empty payload is legitimate. GhostManager, GhostSystemInitializer and
            // TravelManager all have packets that serialise to nothing, because their
            // state is rebuilt on load rather than stored. There is nothing to read, and
            // the cleared packet is already the correct result.
            if (bytes.Length == 0)
            {
                try { packet.Clear(); } catch (Exception) { }
                return true;
            }

            ES2Reader reader = null;
            try
            {
                var settings = new ES2Settings(ES2Settings.SaveLocation.Memory);
                settings.encrypt = false;

                reader = ES2Reader.Create(bytes, settings);
                if (reader == null) { error = "ES2Reader.Create returned null"; return false; }

                // Start from empty. Several packets append to their lists on Load rather
                // than replacing them, so reusing one without clearing would double its
                // contents every time a snapshot arrived.
                try { packet.Clear(); } catch (Exception) { }

                var saveReader = new SaveReader(SaveVersion, reader);
                packet.Cast<ISaveable>().Load(saveReader);   // see the note in TrySerialise
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
            finally
            {
                try { reader?.Dispose(); } catch (Exception) { }
            }
        }

        /// <summary>
        /// Prove the round trip on live managers WITHOUT applying anything.
        /// <para>
        /// This is deliberately read-only. Applying a world snapshot goes through the
        /// game's load path and can disturb a world the player cares about, so the
        /// serialiser gets proved first on its own terms. If this fails, nothing about the
        /// join flow is worth writing yet.
        /// </para>
        /// </summary>
        public static void RunSelfTest(Action<string> log)
        {
            log("--- save serialiser self test (read only, nothing is applied) ---");

            List<IManager> managers;
            try
            {
                managers = FindManagers(log);
            }
            catch (Exception ex)
            {
                log($"  could not enumerate managers: {ex.Message}");
                return;
            }

            if (managers.Count == 0)
            {
                log("  NOTHING TO TEST: no managers exist yet.");
                log("  Load or start a save, wait until you can move your character,");
                log("  then press this again.");
                return;
            }

            var tested = 0;
            var ok = 0;
            var stateless = 0;
            var totalBytes = 0;
            var worldBytes = 0;
            var failures = new StringBuilder();

            try
            {
                foreach (var manager in managers)
                {
                    if (manager == null) continue;

                    var typeName = ManagerName(manager);
                    tested++;

                    ISavePacket packet;
                    try
                    {
                        packet = manager.CreatePacket();
                    }
                    catch (Exception ex)
                    {
                        failures.AppendLine($"    {typeName}: CreatePacket threw {ex.GetType().Name}");
                        continue;
                    }

                    // A null packet is not an error. Plenty of managers implement IManager
                    // for lifecycle reasons and persist nothing: the Articy linkers,
                    // NavigationManager, SeenItemManager and friends. They have no state to
                    // send and must be skipped rather than reported as broken.
                    if (packet == null)
                    {
                        stateless++;
                        continue;
                    }

                    try
                    {
                        manager.WriteToPacket(packet);
                    }
                    catch (Exception ex)
                    {
                        failures.AppendLine($"    {typeName}: WriteToPacket threw {ex.GetType().Name}");
                        continue;
                    }

                    var bytes = TrySerialise(packet, out var writeError);
                    if (bytes == null)
                    {
                        failures.AppendLine($"    {typeName}: write failed, {writeError}");
                        continue;
                    }

                    totalBytes += bytes.Length;

                    // Round trip into a SEPARATE packet so the live one is untouched.
                    ISavePacket scratch;
                    try { scratch = manager.CreatePacket(); }
                    catch (Exception ex)
                    {
                        failures.AppendLine($"    {typeName}: second CreatePacket threw {ex.GetType().Name}");
                        continue;
                    }

                    if (!TryDeserialise(scratch, bytes, out var readError))
                    {
                        var known = ManagerOwnership.KnownBroken.Contains(typeName) ? " (KNOWN)" : "";
                        failures.AppendLine(
                            $"    {typeName}{known}: read failed after {bytes.Length} bytes, " +
                            $"{Shorten(readError)}");
                        continue;
                    }

                    ok++;
                    var owner = ManagerOwnership.Classify(typeName);
                    if (owner == Ownership.World) worldBytes += bytes.Length;

                    log($"    {typeName,-46} {bytes.Length,9:N0} b  {owner}");
                }
            }
            catch (Exception ex)
            {
                log($"  enumeration failed: {ex.Message}");
            }

            var withState = tested - stateless;
            log($"  {ok} of {withState} stateful manager(s) round-tripped, {totalBytes:N0} bytes");
            log($"  {stateless} manager(s) hold no save state and were skipped");
            log($"  WORLD SNAPSHOT would be {worldBytes:N0} bytes ({worldBytes / 1024:N0} KB)");

            if (failures.Length > 0)
            {
                log("  REAL FAILURES:");
                log(failures.ToString().TrimEnd());
            }
            else if (withState > 0)
            {
                log("  every stateful manager serialised and read back cleanly.");
            }
        }

        /// <summary>
        /// Find every live manager.
        /// <para>
        /// <b>Deliberately does not go through <c>ManagersSave</c>.</b> The first version
        /// captured that instance from a Harmony patch on its constructor. Harmony bound
        /// the patch, but the postfix never ran in a loaded world: constructor patching is
        /// a known weak spot under IL2CPP. Depending on it meant the whole snapshot was
        /// unavailable with no obvious cause.
        /// </para>
        /// <para>
        /// Every manager is a <c>Singleton&lt;T&gt; : MonoBehaviour</c> and
        /// <c>CreatePacket</c> / <c>WriteToPacket</c> are declared on <c>IManager</c>
        /// itself, so scanning the scene reaches them directly with nothing in between.
        /// Slower than a cached dictionary, but this runs on a keypress and on a join, not
        /// per frame.
        /// </para>
        /// </summary>
        public static List<IManager> FindManagers(Action<string> log = null)
        {
            var found = new List<IManager>();

            try
            {
                var behaviours = UnityEngine.Object.FindObjectsOfType<UnityEngine.MonoBehaviour>();
                foreach (var b in behaviours)
                {
                    if (b == null) continue;

                    var manager = b.TryCast<IManager>();
                    if (manager != null) found.Add(manager);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  manager scan failed: {ex.Message}");
            }

            // If the constructor capture did happen to work, cross-check the counts. A
            // mismatch means some manager is not a MonoBehaviour and the scan misses it.
            var captured = Patches.ManagersSaveCapture.Current;
            if (captured != null)
            {
                try
                {
                    var expected = captured.Managers.Count;
                    if (expected != found.Count)
                    {
                        log?.Invoke($"  note: scan found {found.Count} manager(s) but ManagersSave " +
                                    $"holds {expected}. Some are not MonoBehaviours.");
                    }
                }
                catch (Exception) { }
            }

            log?.Invoke($"  found {found.Count} manager(s) by scanning the scene");
            return found;
        }

        /// <summary>
        /// Trim an Il2Cpp exception down to its first line. They arrive with a full
        /// native stack trace attached, which buries every other line of the report.
        /// </summary>
        private static string Shorten(string message)
        {
            if (string.IsNullOrEmpty(message)) return "unknown";

            var cut = message.IndexOf("--- BEGIN IL2CPP", StringComparison.Ordinal);
            if (cut > 0) message = message.Substring(0, cut);

            var newline = message.IndexOfAny(new[] { '\r', '\n' });
            if (newline > 0) message = message.Substring(0, newline);

            return message.Trim();
        }

        /// <summary>Public form of <see cref="ManagerName"/>, for the snapshot builder.</summary>
        public static string ManagerNameOf(IManager manager) => ManagerName(manager);

        private static string ManagerName(IManager manager)
        {
            try
            {
                var behaviour = manager.TryCast<UnityEngine.MonoBehaviour>();
                if (behaviour != null) return behaviour.GetIl2CppType().Name;
            }
            catch (Exception) { }

            return "<unknown>";
        }
    }
}
