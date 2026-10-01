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
    }
}
