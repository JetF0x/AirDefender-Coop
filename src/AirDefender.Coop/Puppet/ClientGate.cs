using System;

namespace AirDefenderCoop
{
    /// <summary>
    /// Central switch for "this process is a co-op client mirroring the host". Harmony prefixes
    /// consult <see cref="Suppress"/> to skip local simulation decisions; code that must run a
    /// suppressed path on purpose (replication applying host state) wraps it in
    /// <see cref="AllowLocalAction"/>.
    /// </summary>
    public static class ClientGate
    {
        [ThreadStatic] private static int _allowDepth;

        /// <summary>True when this client is in the host's world and must not simulate on its own.</summary>
        public static bool PuppetActive =>
            CoopSession.IsClient && CoopSession.Connected && Bootstrap.WorldSync.ClientWorldReady;

        /// <summary>True when a gated local simulation path should be skipped right now.</summary>
        public static bool Suppress => PuppetActive && _allowDepth == 0 && !Bootstrap.WorldSync.ApplyingRemoteWorld;

        /// <summary>True while replication is deliberately running gated code.</summary>
        public static bool InAllowedScope => _allowDepth > 0;

        public static void AllowLocalAction(Action a)
        {
            _allowDepth++;
            try { a(); }
            finally { _allowDepth--; }
        }

        public static T AllowLocalAction<T>(Func<T> f)
        {
            _allowDepth++;
            try { return f(); }
            finally { _allowDepth--; }
        }
    }
}
