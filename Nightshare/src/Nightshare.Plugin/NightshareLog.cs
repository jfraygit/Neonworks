using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Nightshare
{
    /// <summary>
    /// Writes Nightshare's own log to a per-process file, alongside the BepInEx logger.
    /// <para>
    /// <b>Why this exists.</b> Two instances of the game on one machine share a single
    /// <c>BepInEx/LogOutput.log</c>, and only the first to start can hold it open. The
    /// second instance's entire log output is silently lost. That makes every two-instance
    /// test undiagnosable: the host's side is visible and the client's simply is not,
    /// which is exactly backwards, because the client is the one doing the interesting
    /// work on a join.
    /// </para>
    /// <para>
    /// Each process writes to its own file named by role and process id, so both halves of
    /// a session can be read side by side afterwards.
    /// </para>
    /// </summary>
    internal static class NightshareLog
    {
        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static string _path;

        public static string Path => _path;

        /// <summary>
        /// Open the log. Role comes from the command line so a host and a client are
        /// distinguishable at a glance rather than by process id alone.
        /// </summary>
        public static void Open(string artifactsDir, string role)
        {
            lock (Gate)
            {
                if (_writer != null) return;

                try
                {
                    Directory.CreateDirectory(artifactsDir);

                    var pid = Process.GetCurrentProcess().Id;
                    var safeRole = string.IsNullOrWhiteSpace(role) ? "game" : role.ToLowerInvariant();
                    _path = System.IO.Path.Combine(artifactsDir, $"nightshare-{safeRole}-{pid}.log");

                    _writer = new StreamWriter(
                        new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(false))
                    {
                        AutoFlush = true,   // a crash must not take the last lines with it
                    };

                    _writer.WriteLine($"Nightshare {NightsharePlugin.Version}  role={safeRole}  pid={pid}");
                    _writer.WriteLine($"started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    _writer.WriteLine(new string('-', 70));
                }
                catch (Exception)
                {
                    // A logger that cannot open must not stop the mod from running.
                    _writer = null;
                    _path = null;
                }
            }
        }

        public static void Write(string level, string message)
        {
            if (_writer == null) return;

            lock (Gate)
            {
                if (_writer == null) return;

                try
                {
                    _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level,-7} {message}");
                }
                catch (Exception)
                {
                    // Never let logging be the thing that breaks a session.
                }
            }
        }

        public static void Close()
        {
            lock (Gate)
            {
                try { _writer?.Flush(); } catch (Exception) { }
                try { _writer?.Dispose(); } catch (Exception) { }
                _writer = null;
            }
        }

        // ------------------------------------------------------------ the game's own log

        private static bool _mirroring;

        /// <summary>
        /// Capture plain Unity <c>Log</c> messages as well, not just problems. Off by
        /// default because the game is chatty; turned on around a diagnostic that reports
        /// through Unity rather than through us.
        /// </summary>
        public static bool CaptureEverything { get; set; }

        /// <summary>
        /// Mirror Unity's log into this process's own file.
        /// <para>
        /// <b>Why this is not optional.</b> BepInEx writes one <c>LogOutput.log</c> and only
        /// the first process to start can hold it open, so the second instance's Unity
        /// output is silently discarded. That instance is the guest, which is the one doing
        /// everything interesting. It cost two separate diagnoses: the game's own
        /// <c>OverrideableBool.LogOwners()</c> was called to name who was holding a pause,
        /// printed perfectly, and went nowhere.
        /// </para>
        /// <para>
        /// Warnings, errors, exceptions and asserts are always kept: they are rare and are
        /// exactly what gets looked for afterwards. Ordinary messages need
        /// <see cref="CaptureEverything"/>.
        /// </para>
        /// </summary>
        public static void MirrorUnityLog()
        {
            if (_mirroring) return;

            try
            {
                UnityEngine.Application.add_logMessageReceived(
                    Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Application.LogCallback>(
                        new Action<string, string, UnityEngine.LogType>(OnUnityLog)));

                _mirroring = true;
                Write("INFO", "Mirroring the game's log into this file");
            }
            catch (Exception ex)
            {
                // Losing the mirror is survivable; losing the mod is not.
                NightsharePlugin.Logger?.LogWarning(
                    $"Could not mirror the Unity log ({ex.GetType().Name}: {ex.Message}). " +
                    $"Game-side messages will only appear in BepInEx's shared log.");
            }
        }

        private static void OnUnityLog(string message, string stackTrace, UnityEngine.LogType type)
        {
            try
            {
                // WARNINGS ARE NOT KEPT BY DEFAULT, DELIBERATELY.
                //
                // This game logs its scene-loading timings at warning level, around a
                // hundred lines per load. Keeping them made a single session's log 76 KB of
                // "LoadAreaRoutine: 00:00:00.00" with the four lines that mattered buried in
                // it, which defeats the point of having the log at all. Errors and
                // exceptions are rare and are what gets looked for.
                var important = type == UnityEngine.LogType.Error
                             || type == UnityEngine.LogType.Exception
                             || type == UnityEngine.LogType.Assert;

                if (!important && !CaptureEverything) return;

                Write($"unity:{type}", message);

                // A stack trace is the whole value of an exception, and only of an exception.
                if (type == UnityEngine.LogType.Exception && !string.IsNullOrEmpty(stackTrace))
                    Write($"unity:{type}", "  " + stackTrace.Replace("\n", "\n  ").TrimEnd());
            }
            catch (Exception)
            {
                // Never let logging be the thing that breaks a session.
            }
        }
    }
}
