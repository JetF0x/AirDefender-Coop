using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace AirDefenderCoop
{
    /// <summary>
    /// Logs to BepInEx and to a per-process file (BepInEx/coop-&lt;pid&gt;.log), so two local
    /// instances never interleave in one log.
    /// </summary>
    public static class CoopLog
    {
        private static ManualLogSource _src;
        private static StreamWriter _file;
        private static readonly object Lock = new object();

        public static string FilePath { get; private set; }

        public static void Init(ManualLogSource src)
        {
            _src = src;
            try
            {
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                FilePath = Path.Combine(Paths.BepInExRootPath, $"coop-{pid}.log");
                _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                CleanupOldLogs(pid);
            }
            catch (Exception e)
            {
                src.LogWarning("coop log file unavailable: " + e.Message);
            }
        }

        // Keep the folder tidy: drop per-process logs older than a day.
        private static void CleanupOldLogs(int pid)
        {
            try
            {
                foreach (var f in Directory.GetFiles(Paths.BepInExRootPath, "coop-*.log"))
                {
                    if (f.EndsWith($"coop-{pid}.log")) continue;
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f);
                }
            }
            catch { }
        }

        private static void Write(string level, string msg)
        {
            lock (Lock)
            {
                try { _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] [{CoopSession.RoleTag}] {msg}"); } catch { }
            }
        }

        public static void Info(string msg) { _src?.LogInfo(msg); Write("INFO", msg); }
        public static void Warn(string msg) { _src?.LogWarning(msg); Write("WARN", msg); }
        public static void Error(string msg) { _src?.LogError(msg); Write("ERROR", msg); }

        /// <summary>File-only, for high-volume diagnostics.</summary>
        public static void Debug(string msg) { if (CoopConfig.VerboseLog) Write("DEBUG", msg); }
    }
}
