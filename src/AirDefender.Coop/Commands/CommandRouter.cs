using System;
using System.Collections.Generic;
using AirDefenderCoop.Net;

namespace AirDefenderCoop.Commands
{
    /// <summary>
    /// Player actions taken on the client are not executed locally: a Harmony prefix captures the
    /// call and sends it here, the host runs the real game method, and the results replicate back.
    /// </summary>
    public static class CommandRouter
    {
        private static readonly Dictionary<string, Action<NetReader>> Handlers = new Dictionary<string, Action<NetReader>>(StringComparer.Ordinal);

        [ThreadStatic] private static int _remoteDepth;

        /// <summary>True while the host executes a command that came from the partner.</summary>
        public static bool ExecutingRemote => _remoteDepth > 0;

        internal static void EnterRemote() => _remoteDepth++;
        internal static void ExitRemote() => _remoteDepth--;

        public static int Sent { get; private set; }
        public static int Executed { get; private set; }
        public static int Failed { get; private set; }

        public static void Init()
        {
            CoopSession.Register(MsgType.Command, OnCommand);
        }

        public static void Register(string name, Action<NetReader> hostExecute) => Handlers[name] = hostExecute;

        /// <summary>
        /// Client: forward a player action to the host. Returns true when it was routed, meaning the
        /// local call must be skipped.
        /// </summary>
        public static bool Route(string name, Action<NetWriter> args)
        {
            if (!ClientGate.Suppress) return false;
            CoopSession.Send(MsgType.Command, w => { w.Str(name); args?.Invoke(w); });
            Sent++;
            CoopLog.Info($"Command -> host: {name}");
            return true;
        }

        private static void OnCommand(NetReader r)
        {
            if (!CoopSession.IsHost) return;
            string name = r.Str();
            if (!Handlers.TryGetValue(name, out var h))
            {
                CoopLog.Warn($"{CoopSession.SenderName} sent unknown command {name}");
                return;
            }
            _remoteDepth++;
            try
            {
                h(r);
                Executed++;
                CoopLog.Info($"Executed {CoopSession.SenderName}'s command {name}");
            }
            catch (Exception e)
            {
                Failed++;
                CoopLog.Error($"{CoopSession.SenderName}'s command {name} failed: {e}");
            }
            finally { _remoteDepth--; }
        }
    }
}
