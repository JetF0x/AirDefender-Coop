using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using AirDefender;
using AirDefenderCoop.Net;
using AirDefenderCoop.Replication;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop.Commands
{
    /// <summary>
    /// Generic remote execution of game methods. On the client a Harmony prefix captures the call
    /// (instance + arguments), the host runs the real method and replies with the return value and
    /// any out-parameters. Calls without results are fire-and-forget; calls with results block the
    /// client briefly (one round trip) so the calling UI code continues with the host's real outcome.
    /// </summary>
    public static class MethodRouter
    {
        private const float BlockingTimeout = 3f;

        internal enum InstanceKind : byte { Static, Entity, Track, Singleton }

        internal sealed class Route
        {
            public int Id;
            public MethodInfo Method;
            public ParameterInfo[] Params;
            public InstanceKind Instance;
            public bool Blocking;
            public string Name;
        }

        private static readonly List<Route> Routes = new List<Route>();
        private static readonly Dictionary<MethodBase, Route> ByMethod = new Dictionary<MethodBase, Route>();
        private static readonly Dictionary<int, NetReader> Results = new Dictionary<int, NetReader>();
        private static int _nextRequest;
        [ThreadStatic] private static int _waitDepth;

        public static int RouteCount => Routes.Count;
        public static int Calls { get; private set; }
        public static int Timeouts { get; private set; }
        public static float LastBlockMs { get; private set; }

        public static void Init()
        {
            CoopSession.Register(MsgType.RouteCall, OnRouteCall);
            CoopSession.Register(MsgType.CommandResult, OnResult);
        }

        /// <summary>Registers and patches one game method. Order of registration must match on both peers.</summary>
        public static void Add(Harmony h, Type type, string method, Type[] args = null, bool? blocking = null)
        {
            MethodInfo mi = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
            if (mi == null) { CoopLog.Warn($"Route: {type.Name}.{method} not found"); Routes.Add(null); return; }
            var ps = mi.GetParameters();
            foreach (var p in ps)
            {
                Type pt = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                if (!p.IsOut && !ArgCodec.CanEncode(pt)) { CoopLog.Warn($"Route: {type.Name}.{method} has unsupported parameter {p.Name}:{pt.Name}"); Routes.Add(null); return; }
            }
            InstanceKind ik = InstanceKind.Static;
            if (!mi.IsStatic)
            {
                if (type == typeof(TrackRenderer)) ik = InstanceKind.Track;
                else if (ArgCodec.IsEntityType(type)) ik = InstanceKind.Entity;
                else ik = InstanceKind.Singleton;
            }
            bool hasResult = mi.ReturnType != typeof(void) || ps.Any(p => p.ParameterType.IsByRef);
            var route = new Route
            {
                Id = Routes.Count, Method = mi, Params = ps, Instance = ik,
                Blocking = blocking ?? hasResult, Name = $"{type.Name}.{method}"
            };
            Routes.Add(route);
            ByMethod[mi] = route;
            string prefix = mi.ReturnType == typeof(void) ? nameof(PrefixVoid) : nameof(PrefixResult);
            try { h.Patch(mi, prefix: new HarmonyMethod(typeof(MethodRouter), prefix) { priority = Priority.First }); }
            catch (Exception e)
            {
                // Keep the slot so later route ids stay aligned between peers.
                CoopLog.Error($"Route: could not patch {route.Name}: {e.Message}");
                ByMethod.Remove(mi);
                Routes[route.Id] = null;
            }
        }

        // ================================================================ client side

        private static bool PrefixVoid(MethodBase __originalMethod, object __instance, object[] __args)
        {
            object ignored = null;
            return Intercept(__originalMethod, __instance, __args, ref ignored);
        }

        private static bool PrefixResult(MethodBase __originalMethod, object __instance, object[] __args, ref object __result)
        {
            return Intercept(__originalMethod, __instance, __args, ref __result);
        }

        private static bool Intercept(MethodBase __originalMethod, object __instance, object[] __args, ref object __result)
        {
            if (!ClientGate.Suppress) return true;
            if (!ByMethod.TryGetValue(__originalMethod, out var route)) return true;
            Calls++;
            var w = new NetWriter(256);
            w.U16((ushort)MsgType.RouteCall);
            w.U16((ushort)route.Id);
            int req = route.Blocking ? ++_nextRequest : 0;
            w.I32(req);
            try
            {
                WriteInstance(w, route, __instance);
                for (int i = 0; i < route.Params.Length; i++)
                {
                    if (route.Params[i].IsOut) continue;
                    ArgCodec.Write(w, route.Params[i].ParameterType, __args[i]);
                }
            }
            catch (Exception e)
            {
                CoopLog.Error($"Cannot route {route.Name}: {e.Message}");
                Ui.CoopToast.Show("That action is not available to the co-op partner yet");
                __result = DefaultOf(route.Method.ReturnType);
                return false;
            }
            CoopSession.SendRaw(w.ToArray(), w.Length, reliable: true);
            CoopLog.Info($"Routed {route.Name} to host{(route.Blocking ? $" (waiting, req {req})" : "")}");

            __result = DefaultOf(route.Method.ReturnType);
            if (!route.Blocking) return false;

            var reader = WaitForResult(req);
            if (reader == null)
            {
                Timeouts++;
                Ui.CoopToast.Show("The host did not respond in time");
                for (int i = 0; i < route.Params.Length; i++)
                    if (route.Params[i].IsOut) __args[i] = DefaultOf(route.Params[i].ParameterType.GetElementType());
                return false;
            }
            try
            {
                bool ok = reader.Bool();
                if (!ok)
                {
                    string err = reader.Str();
                    CoopLog.Warn($"Host could not run {route.Name}: {err}");
                    return false;
                }
                if (route.Method.ReturnType != typeof(void)) __result = ArgCodec.Read(reader, route.Method.ReturnType);
                for (int i = 0; i < route.Params.Length; i++)
                    if (route.Params[i].ParameterType.IsByRef) __args[i] = ArgCodec.Read(reader, route.Params[i].ParameterType);
            }
            catch (Exception e) { CoopLog.Error($"Bad result for {route.Name}: {e}"); }
            return false;
        }

        private static NetReader WaitForResult(int req)
        {
            var sw = Stopwatch.StartNew();
            _waitDepth++;
            try
            {
                while (sw.Elapsed.TotalSeconds < BlockingTimeout && CoopSession.Connected)
                {
                    CoopSession.Tick();
                    if (Results.TryGetValue(req, out var r)) { Results.Remove(req); return r; }
                    System.Threading.Thread.Sleep(1);
                }
                return null;
            }
            finally
            {
                _waitDepth--;
                LastBlockMs = (float)sw.Elapsed.TotalMilliseconds;
            }
        }

        private static void OnResult(NetReader r)
        {
            int req = r.I32();
            Results[req] = r;
        }

        private static void WriteInstance(NetWriter w, Route route, object inst)
        {
            switch (route.Instance)
            {
                case InstanceKind.Entity: w.Str(ArgCodec.ContactIdOf(inst)); break;
                case InstanceKind.Track: w.Str((inst as TrackRenderer)?.ContactId); break;
            }
        }

        private static object DefaultOf(Type t) => t == typeof(void) || !t.IsValueType ? null : Activator.CreateInstance(t);

        // ================================================================ host side

        private static void OnRouteCall(NetReader r)
        {
            if (!CoopSession.IsHost) return;
            int id = r.U16();
            int req = r.I32();
            var route = id < Routes.Count ? Routes[id] : null;
            if (route == null) { Reply(req, false, "unknown route " + id, null); return; }

            object inst = null;
            string err = null;
            object[] args = new object[route.Params.Length];
            try
            {
                switch (route.Instance)
                {
                    case InstanceKind.Entity:
                        inst = ArgCodec.ResolveEntity(r.Str(), route.Method.DeclaringType);
                        if (inst == null) err = "target no longer exists";
                        break;
                    case InstanceKind.Track:
                        inst = TrackManager.GetRendererForContact(r.Str());
                        if (inst == null) err = "track no longer exists";
                        break;
                    case InstanceKind.Singleton:
                        inst = UnityEngine.Object.FindAnyObjectByType(route.Method.DeclaringType);
                        if (inst == null) err = "host has no " + route.Method.DeclaringType.Name;
                        break;
                }
                for (int i = 0; i < route.Params.Length; i++)
                    args[i] = route.Params[i].IsOut ? null : ArgCodec.Read(r, route.Params[i].ParameterType);
            }
            catch (Exception e) { err = "bad arguments: " + e.Message; }

            if (err != null)
            {
                CoopLog.Warn($"Partner call {route.Name} rejected: {err}");
                if (route.Blocking) Reply(req, false, err, null);
                return;
            }

            object ret = null;
            try
            {
                CommandRouter.EnterRemote();
                ret = route.Method.Invoke(inst, args);
                CoopLog.Info($"Executed partner call {route.Name}");
            }
            catch (TargetInvocationException tie)
            {
                err = tie.InnerException?.Message ?? tie.Message;
                CoopLog.Error($"Partner call {route.Name} threw: {tie.InnerException}");
            }
            finally { CommandRouter.ExitRemote(); }

            if (!route.Blocking) return;
            if (err != null) { Reply(req, false, err, null); return; }

            // Anything the call created must exist on the client before it reads the result.
            var created = new List<string>();
            void Note(Type t, object v) { if (v != null && (ArgCodec.IsEntityType(t) || t == typeof(GameObject) || t == typeof(Transform))) { var cid = ArgCodec.ContactIdOf(v); if (cid != null) created.Add(cid); } }
            if (route.Method.ReturnType != typeof(void)) Note(route.Method.ReturnType, ret);
            for (int i = 0; i < route.Params.Length; i++)
                if (route.Params[i].ParameterType.IsByRef) Note(route.Params[i].ParameterType.GetElementType(), args[i]);
            if (created.Count > 0) EntityReplicator.HostEnsureSpawned(created);

            Reply(req, true, null, w =>
            {
                if (route.Method.ReturnType != typeof(void)) ArgCodec.Write(w, route.Method.ReturnType, ret);
                for (int i = 0; i < route.Params.Length; i++)
                    if (route.Params[i].ParameterType.IsByRef) ArgCodec.Write(w, route.Params[i].ParameterType, args[i]);
            });
        }

        private static void Reply(int req, bool ok, string err, Action<NetWriter> body)
        {
            if (req == 0) return;
            CoopSession.Send(MsgType.CommandResult, w =>
            {
                w.I32(req);
                w.Bool(ok);
                if (!ok) w.Str(err ?? "failed");
                else body?.Invoke(w);
            });
        }
    }
}
