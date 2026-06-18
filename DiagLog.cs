using System;
using System.Globalization;
using System.IO;

namespace Trashville
{
    /// <summary>
    /// Crash-resilient diagnostic logging. MelonLogger buffers, so a HARD native crash loses the last
    /// lines - which is exactly why we never see the death point. DiagLog flushes to DISK on every write
    /// (Note) and rewrites a tiny heartbeat file every frame (Heartbeat). After a crash these files hold
    /// the exact last state. Files: Mods/Trashville/diag.log and Mods/Trashville/heartbeat.txt.
    /// </summary>
    internal static class DiagLog
    {
        private static FileStream _fs;
        private static StreamWriter _w;
        private static string _heartbeatPath;
        private static bool _failed;

        private static void Ensure()
        {
            if (_w != null || _failed)
            {
                return;
            }
            try
            {
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "Trashville");
                Directory.CreateDirectory(dir);
                _heartbeatPath = Path.Combine(dir, "heartbeat.txt");
                _fs = new FileStream(Path.Combine(dir, "diag.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _w = new StreamWriter(_fs) { AutoFlush = true };
                Note("==== DiagLog opened ====");
            }
            catch
            {
                _failed = true;
            }
        }

        /// <summary>Append one line and force it to DISK (survives a hard crash on the next instruction).</summary>
        internal static void Note(string msg)
        {
            Ensure();
            if (_w == null)
            {
                return;
            }
            try
            {
                _w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + msg);
                _fs.Flush(true);   // flush OS buffers to physical disk
            }
            catch { /* never let diagnostics throw */ }
        }

        /// <summary>Overwrite the heartbeat file with the current state (tiny, flushed on close).</summary>
        internal static void Heartbeat(string msg)
        {
            Ensure();
            if (_heartbeatPath == null)
            {
                return;
            }
            try
            {
                File.WriteAllText(_heartbeatPath, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + msg);
            }
            catch { /* ignore */ }
        }
    }
}
