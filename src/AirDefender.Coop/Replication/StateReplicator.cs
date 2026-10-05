using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AirDefender;
using AirDefenderCoop.Net;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Field-level state sync. The host walks every game component on every contact (and a list of
    /// global services), encodes each replicable field, and sends only fields whose encoding
    /// changed since the last send. The client writes the values straight into its puppets.
    /// </summary>
    public static class StateReplicator
    {
        private const float EntityPeriod = 1.0f;  // each contact refreshed once a second
        private const float GlobalPeriod = 1.0f;

        // Stable ids for every game component type (same build on both peers -> same order).
        private static Type[] _types;
        private static Dictionary<Type, ushort> _typeIds;

        private sealed class CompState
        {
            public ulong[] Hashes;
            public bool Enabled;
            public bool Sent;
        }

        private sealed class EntityState
        {
            public string CompSig;
            public readonly Dictionary<ushort, CompState> Comps = new Dictionary<ushort, CompState>();
        }

        private static readonly Dictionary<string, EntityState> Entities = new Dictionary<string, EntityState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, CompState> Globals = new Dictionary<string, CompState>(StringComparer.Ordinal);
        private static readonly List<IContact> Work = new List<IContact>();
        private static readonly NetWriter Scratch = new NetWriter(256);
        private static readonly NetWriter Msg = new NetWriter(8192);
        private static int _cursor;
        private static float _nextGlobal;
        private static bool _primed;
        private static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);

        public static long FieldsSent { get; private set; }
        public static long FieldsApplied { get; private set; }
        public static double HostMsPerSecond { get; private set; }
        private static double _msAccum;
        private static float _msWindowStart;

        public static void Init()
        {
            var game = typeof(TrackManager).Assembly;
            _types = game.GetTypes()
                .Where(t => typeof(MonoBehaviour).IsAssignableFrom(t) && !t.IsAbstract && !t.IsGenericTypeDefinition)
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            _typeIds = new Dictionary<Type, ushort>();
            for (int i = 0; i < _types.Length; i++) _typeIds[_types[i]] = (ushort)i;

            CoopSession.Register(MsgType.EntityFields, OnEntityFields);
            CoopSession.Register(MsgType.GlobalState, OnGlobalState);
            Bootstrap.WorldSync.PartnerContactsReported += _ => { Entities.Clear(); Globals.Clear(); _primed = true; };
            Bootstrap.WorldSync.HostWorldSent += () => _primed = false;
            CoopSession.PartnerLeft += _ => { Entities.Clear(); Globals.Clear(); _primed = false; };
        }

        // ================================================================ host

        public static void HostTick()
        {
            if (!_primed || !Bootstrap.WorldSync.PartnerWorldReady) return;
            var sw = Stopwatch.StartNew();

            // Rebuild the work list once per sweep; process a slice each frame.
            if (_cursor == 0 || _cursor >= Work.Count)
            {
                Work.Clear();
                Work.AddRange(ContactRegistry.GetContactsListSnapshot());
                _cursor = 0;
                PruneEntities();
            }
            int perFrame = Mathf.Max(1, Mathf.CeilToInt(Work.Count * Time.unscaledDeltaTime / EntityPeriod));
            int end = Mathf.Min(Work.Count, _cursor + perFrame);
            for (; _cursor < end; _cursor++)
            {
                var c = Work[_cursor];
                if (c == null || EntityReplicator.IsLocalAuthority(c)) continue;
                var mb = c as MonoBehaviour;
                if (mb == null || mb.Equals(null)) continue;
                string id;
                try { id = c.ContactId; } catch { continue; }
                if (string.IsNullOrEmpty(id) || !EntityReplicator.HostHasSpawned(id)) continue;
                SendEntity(id, mb.gameObject);
            }
            if (_cursor >= Work.Count) _cursor = 0;

            if (Time.realtimeSinceStartup >= _nextGlobal)
            {
                _nextGlobal = Time.realtimeSinceStartup + GlobalPeriod;
                SendGlobals();
            }

            _msAccum += sw.Elapsed.TotalMilliseconds;
            if (Time.realtimeSinceStartup - _msWindowStart >= 5f)
            {
                HostMsPerSecond = _msAccum / (Time.realtimeSinceStartup - _msWindowStart);
                _msAccum = 0;
                _msWindowStart = Time.realtimeSinceStartup;
            }
        }

        private static void PruneEntities()
        {
            Seen.Clear();
            foreach (var c in Work) { try { if (c != null) Seen.Add(c.ContactId); } catch { } }
            var dead = Entities.Keys.Where(k => !Seen.Contains(k)).ToList();
            foreach (var k in dead) Entities.Remove(k);
        }

        private static void SendEntity(string id, GameObject go)
        {
            if (!Entities.TryGetValue(id, out var es)) { es = new EntityState(); Entities[id] = es; }
            var comps = go.GetComponents<MonoBehaviour>();
            var ids = new List<ushort>(comps.Length);
            foreach (var comp in comps)
                if (comp != null && _typeIds.TryGetValue(comp.GetType(), out ushort tid) && !ids.Contains(tid)) ids.Add(tid);
            ids.Sort();
            string sig = string.Join(",", ids);

            Msg.Reset();
            Msg.U16((ushort)MsgType.EntityFields);
            Msg.Str(id);
            bool sigChanged = sig != es.CompSig;
            Msg.Bool(sigChanged);
            if (sigChanged)
            {
                Msg.U16((ushort)ids.Count);
                foreach (var t in ids) Msg.U16(t);
                es.CompSig = sig;
            }
            int countPos = Msg.Length;
            Msg.U16(0);
            int compsWritten = 0;
            foreach (var tid in ids)
            {
                var comp = go.GetComponent(_types[tid]) as MonoBehaviour;
                if (comp == null) continue;
                if (!es.Comps.TryGetValue(tid, out var cs)) { cs = new CompState(); es.Comps[tid] = cs; }
                if (WriteDelta(Msg, tid, comp, TypeSchema.Get(_types[tid], statics: false), cs)) compsWritten++;
            }
            if (compsWritten == 0 && !sigChanged) return;
            Msg.Buffer[countPos] = (byte)compsWritten;
            Msg.Buffer[countPos + 1] = (byte)(compsWritten >> 8);
            CoopSession.SendRaw(Msg.ToArray(), Msg.Length, reliable: true);
        }

        /// <summary>Appends [typeId, enabled, n, (idx,value)*] when anything changed. Returns whether it wrote.</summary>
        private static bool WriteDelta(NetWriter m, ushort tid, object target, TypeSchema schema, CompState cs)
        {
            var fields = schema.Fields;
            if (cs.Hashes == null || cs.Hashes.Length != fields.Length) { cs.Hashes = new ulong[fields.Length]; cs.Sent = false; }
            bool enabled = !(target is Behaviour b) || b.enabled;
            int start = m.Length;
            m.U16(tid);
            m.Bool(enabled);
            int nPos = m.Length;
            m.U16(0);
            int n = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                object v;
                try { v = f.Field.GetValue(target); }
                catch { continue; }
                Scratch.Reset();
                try { FieldCodec.Write(Scratch, f, v); }
                catch { continue; }
                ulong h = FieldCodec.Hash(Scratch.Buffer, 0, Scratch.Length);
                if (cs.Sent && cs.Hashes[i] == h) continue;
                cs.Hashes[i] = h;
                m.U16(f.Index);
                m.Bytes(Scratch.Buffer, 0, Scratch.Length);
                n++;
            }
            bool enabledChanged = !cs.Sent || cs.Enabled != enabled;
            cs.Enabled = enabled;
            cs.Sent = true;
            if (n == 0 && !enabledChanged)
            {
                m.Truncate(start);
                return false;
            }
            m.Buffer[nPos] = (byte)n;
            m.Buffer[nPos + 1] = (byte)(n >> 8);
            FieldsSent += n;
            return true;
        }

        private static void SendGlobals()
        {
            foreach (var g in GlobalTargets.All)
            {
                object instance;
                try { instance = g.Instance(); }
                catch { continue; }
                if (!g.IsStatic && instance == null) continue;
                var schema = TypeSchema.Get(g.Type, g.IsStatic);
                if (schema.Fields.Length == 0) continue;
                if (!Globals.TryGetValue(g.Key, out var cs)) { cs = new CompState(); Globals[g.Key] = cs; }
                Msg.Reset();
                Msg.U16((ushort)MsgType.GlobalState);
                Msg.Str(g.Key);
                if (!WriteDelta(Msg, 0, instance, schema, cs)) continue;
                CoopSession.SendRaw(Msg.ToArray(), Msg.Length, reliable: true);
            }
        }

        // ================================================================ client

        private static void OnEntityFields(NetReader r)
        {
            string id = r.Str();
            bool hasSig = r.Bool();
            List<ushort> sig = null;
            if (hasSig)
            {
                int n = r.U16();
                sig = new List<ushort>(n);
                for (int i = 0; i < n; i++) sig.Add(r.U16());
            }
            int comps = r.U16();
            if (!ClientGate.PuppetActive) return;
            if (!ContactRegistry.TryGetContact(id, out var c)) return;
            var mb = c as MonoBehaviour;
            if (mb == null || mb.Equals(null)) return;
            var go = mb.gameObject;

            ClientGate.AllowLocalAction(() =>
            {
                if (sig != null) ReconcileComponents(go, sig);
                for (int i = 0; i < comps; i++)
                {
                    ushort tid = r.U16();
                    Type t = tid < _types.Length ? _types[tid] : null;
                    var target = t != null ? go.GetComponent(t) : null;
                    ApplyDelta(r, target, t != null ? TypeSchema.Get(t, false) : null);
                }
            });
        }

        private static void ReconcileComponents(GameObject go, List<ushort> sig)
        {
            var want = new HashSet<Type>(sig.Where(t => t < _types.Length).Select(t => _types[t]));
            foreach (var comp in go.GetComponents<MonoBehaviour>())
            {
                if (comp == null || comp is CoopPuppet) continue;
                var t = comp.GetType();
                if (_typeIds.ContainsKey(t) && !want.Contains(t))
                {
                    CoopLog.Debug($"{go.name}: removing component {t.Name} (host has none)");
                    UnityEngine.Object.Destroy(comp);
                }
            }
            foreach (var t in want)
            {
                if (go.GetComponent(t) != null) continue;
                try
                {
                    go.AddComponent(t);
                    CoopLog.Debug($"{go.name}: added component {t.Name} from host");
                }
                catch (Exception e) { CoopLog.Warn($"{go.name}: could not add {t.Name}: {e.Message}"); }
            }
        }

        /// <summary>Reads one [typeId-less] delta block: enabled, n, (idx,value)*. A null target consumes and discards.</summary>
        private static void ApplyDelta(NetReader r, object target, TypeSchema schema)
        {
            bool enabled = r.Bool();
            int n = r.U16();
            if (target is Behaviour b && b.enabled != enabled) b.enabled = enabled;
            for (int i = 0; i < n; i++)
            {
                ushort idx = r.U16();
                byte[] payload = r.Bytes();
                if (schema == null || idx >= schema.Fields.Length) continue;
                var f = schema.Fields[idx];
                if (!f.Field.IsStatic && target == null) continue;
                try
                {
                    var vr = new NetReader(payload);
                    object v = FieldCodec.Read(vr, f, out bool skip);
                    if (skip) continue;
                    if (f.Field.IsStatic) f.Field.SetValue(null, v);
                    else if (target != null) f.Field.SetValue(target, v);
                    FieldsApplied++;
                }
                catch (Exception e)
                {
                    CoopLog.Debug($"apply {schema.Type.Name}.{f.Name} failed: {e.Message}");
                }
            }
        }

        private static void OnGlobalState(NetReader r)
        {
            string key = r.Str();
            r.U16(); // type id slot (unused for globals)
            if (!ClientGate.PuppetActive) return;
            var g = GlobalTargets.Find(key);
            if (g == null) return;
            object instance = null;
            try { instance = g.Instance(); } catch { }
            if (!g.IsStatic && instance == null) return;
            var schema = TypeSchema.Get(g.Type, g.IsStatic);
            ClientGate.AllowLocalAction(() => ApplyDelta(r, instance, schema));
            g.AfterApply?.Invoke();
        }
    }
}
