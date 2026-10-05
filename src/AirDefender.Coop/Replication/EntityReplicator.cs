using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AirDefender;
using AirDefenderCoop.Bootstrap;
using AirDefenderCoop.Net;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Keeps the client's set of contacts identical to the host's and streams their motion.
    /// Contact ids (IContact.ContactId) are the network identity.
    /// </summary>
    public static class EntityReplicator
    {
        private const float StreamInterval = 0.2f;   // 5 Hz positions (radar sweeps are seconds apart)
        private const float SpawnDelay = 0.35f;       // let a new entity finish initialising
        private const int MaxPerMessage = 48;

        private sealed class HostEntry
        {
            public Vector3 LastPos;
            public float LastTime;
            public bool Spawned;
            public float FirstSeen;
            public int SpawnAttempts;
        }

        // Host state
        private static readonly Dictionary<string, HostEntry> Known = new Dictionary<string, HostEntry>(StringComparer.Ordinal);
        private static readonly HashSet<string> SeenThisScan = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> Scratch = new List<string>();
        private static float _nextStream;
        private static bool _hostPrimed;

        // Client state
        private static readonly HashSet<string> Sanctioned = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<GameObject> RogueToDestroy = new List<GameObject>();
        private static float _adoptAt = -1f;
        private static bool _adopted;

        public static int HostTracked => Known.Count;

        /// <summary>Host: true once the partner has (or is being sent) this contact.</summary>
        public static bool HostHasSpawned(string id) => Known.TryGetValue(id, out var e) && e.Spawned;
        public static int SpawnsSent { get; private set; }
        public static int SpawnsApplied { get; private set; }
        public static int SpawnFailures { get; private set; }
        public static int RogueSpawns { get; private set; }

        public static void Init()
        {
            CoopSession.Register(MsgType.EntitySpawn, OnSpawn);
            CoopSession.Register(MsgType.EntityDespawn, OnDespawn);
            CoopSession.Register(MsgType.EntityStates, OnStates);
            WorldSync.HostWorldSent += () => { Known.Clear(); _hostPrimed = false; };
            WorldSync.ClientWorldLoaded += () => { _adopted = false; _adoptAt = Time.realtimeSinceStartup + 1.0f; };
            WorldSync.PartnerContactsReported += HostPrime;
            CoopSession.PartnerLeft += _ => { Known.Clear(); _hostPrimed = false; Sanctioned.Clear(); };
        }

        /// <summary>Contacts each side simulates for itself (deterministic from the shared clock).</summary>
        public static bool IsLocalAuthority(IContact c)
        {
            string n = c.GetType().Name;
            return n == "SatelliteContact" || n == "SatelliteMover";
        }

        // ================================================================ host

        /// <summary>Host: the client reported the contacts it has after loading; diff against ours.</summary>
        private static void HostPrime(List<string> clientIds)
        {
            Known.Clear();
            var clientSet = new HashSet<string>(clientIds, StringComparer.Ordinal);
            float now = Time.time;
            foreach (var c in ContactRegistry.GetAllContacts())
            {
                if (c == null || IsLocalAuthority(c)) continue;
                string id = SafeId(c);
                if (id == null) continue;
                bool has = clientSet.Remove(id);
                Known[id] = new HostEntry { LastPos = c.transform.position, LastTime = now, Spawned = has, FirstSeen = Time.realtimeSinceStartup };
            }
            // Anything the client has that we do not must go.
            foreach (var extra in clientSet) SendDespawn(extra);
            _hostPrimed = true;
            CoopLog.Info($"Entity replication primed: {Known.Count} host contacts, {clientSet.Count} stale on client");
        }

        public static void HostTick()
        {
            if (!_hostPrimed || !WorldSync.PartnerWorldReady) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextStream) return;
            _nextStream = now + StreamInterval;

            SeenThisScan.Clear();
            var w = new NetWriter(4096);
            int inBatch = 0;
            float t = Time.time;
            List<IContact> contacts = ContactRegistry.GetContactsListSnapshot();
            bool needSpawns = false;
            for (int i = 0; i < contacts.Count; i++)
            {
                var c = contacts[i];
                if (c == null || IsLocalAuthority(c)) continue;
                var mb = c as MonoBehaviour;
                if (mb == null || mb.Equals(null)) continue;
                string id = SafeId(c);
                if (id == null || !SeenThisScan.Add(id)) continue;

                Transform tr = mb.transform;
                Vector3 pos = tr.position;
                if (!Known.TryGetValue(id, out var e))
                {
                    e = new HostEntry { LastPos = pos, LastTime = t, FirstSeen = now };
                    Known[id] = e;
                }
                if (!e.Spawned)
                {
                    if (now - e.FirstSeen >= SpawnDelay) needSpawns = true;
                    continue;
                }

                float dt = t - e.LastTime;
                Vector3 vel = dt > 1e-4f ? (pos - e.LastPos) / dt : Vector3.zero;
                if (vel.sqrMagnitude > 1e8f) vel = Vector3.zero; // teleport, not motion
                e.LastPos = pos;
                e.LastTime = t;

                if (inBatch == 0) { w.Reset(); w.U16((ushort)MsgType.EntityStates); w.U16(0); }
                w.Str(id);
                w.Vec3(pos);
                w.F32(vel.x);
                w.F32(vel.y);
                w.U16((ushort)Mathf.RoundToInt(Mathf.Repeat(tr.eulerAngles.z, 360f) * (65535f / 360f)));
                inBatch++;
                if (inBatch >= MaxPerMessage) { Flush(w, inBatch); inBatch = 0; }
            }
            if (inBatch > 0) Flush(w, inBatch);

            // Removed contacts
            Scratch.Clear();
            foreach (var kv in Known) if (!SeenThisScan.Contains(kv.Key)) Scratch.Add(kv.Key);
            foreach (var id in Scratch)
            {
                if (Known[id].Spawned) SendDespawn(id);
                Known.Remove(id);
            }

            if (needSpawns) SendPendingSpawns(now);
        }

        /// <summary>
        /// Host: send spawn records for these contacts right now (used before replying to a partner
        /// command whose result refers to entities it just created).
        /// </summary>
        public static void HostEnsureSpawned(List<string> ids)
        {
            var need = ids.Where(id => !(Known.TryGetValue(id, out var e) && e.Spawned)).Distinct().ToList();
            if (need.Count == 0) return;
            Dictionary<string, SpawnCatalog.Record> records;
            try { records = SpawnCatalog.Capture(); }
            catch (Exception ex) { CoopLog.Error("Spawn capture failed: " + ex); return; }
            float now = Time.realtimeSinceStartup;
            foreach (var id in need)
            {
                if (!records.TryGetValue(id, out var r)) { CoopLog.Warn($"No spawn record for new entity {id}"); continue; }
                CoopSession.Send(MsgType.EntitySpawn, w => { w.U8((byte)r.Kind); w.Str(r.Id); w.Str(r.Json); });
                SpawnsSent++;
                var pos = ContactRegistry.TryGetContact(id, out var c) && c is MonoBehaviour mb && mb != null ? mb.transform.position : Vector3.zero;
                Known[id] = new HostEntry { LastPos = pos, LastTime = Time.time, Spawned = true, FirstSeen = now };
            }
        }

        private static void Flush(NetWriter w, int count)
        {
            // Patch the count after the message type.
            w.Buffer[2] = (byte)count;
            w.Buffer[3] = (byte)(count >> 8);
            CoopSession.SendRaw(w.ToArray(), w.Length, reliable: false);
        }

        private static void SendDespawn(string id)
        {
            CoopSession.Send(MsgType.EntityDespawn, w => w.Str(id));
        }

        private static void SendPendingSpawns(float now)
        {
            var sw = Stopwatch.StartNew();
            Dictionary<string, SpawnCatalog.Record> records;
            try { records = SpawnCatalog.Capture(); }
            catch (Exception e) { CoopLog.Error("Spawn capture failed: " + e); return; }

            foreach (var kv in Known)
            {
                var e = kv.Value;
                if (e.Spawned || now - e.FirstSeen < SpawnDelay) continue;
                if (records.TryGetValue(kv.Key, out var r))
                {
                    CoopSession.Send(MsgType.EntitySpawn, w => { w.U8((byte)r.Kind); w.Str(r.Id); w.Str(r.Json); });
                    e.Spawned = true;
                    SpawnsSent++;
                }
                else if (++e.SpawnAttempts >= 2 && ContactRegistry.TryGetContact(kv.Key, out var c) && c is MonoBehaviour cmb && cmb != null)
                {
                    // Not described by the save format: send a structural copy; field sync fills it in.
                    var rc = SpawnCatalog.Clone(c);
                    CoopSession.Send(MsgType.EntitySpawn, w => { w.U8((byte)rc.Kind); w.Str(rc.Id); w.Str(rc.Json); });
                    e.Spawned = true;
                    SpawnsSent++;
                    CoopLog.Info($"Spawned {kv.Key} ({c.GetType().Name}) as a structural copy");
                }
            }
            CoopLog.Debug($"spawn capture {sw.ElapsedMilliseconds} ms");
        }

        private static string SafeId(IContact c)
        {
            try { string id = c.ContactId; return string.IsNullOrEmpty(id) ? null : id; }
            catch { return null; }
        }

        // ================================================================ client

        // Runs ~1 s after the world load so restored entities have finished registering.
        private static void ClientAdoptLoadedWorld()
        {
            _adopted = true;
            Sanctioned.Clear();
            var ids = new List<string>();
            foreach (var c in ContactRegistry.GetAllContacts())
            {
                if (c == null || IsLocalAuthority(c)) continue;
                string id = SafeId(c);
                if (id == null) continue;
                Sanctioned.Add(id);
                ids.Add(id);
            }
            WorldSync.ReportClientContacts(ids);
        }

        private static void OnSpawn(NetReader r)
        {
            var kind = (SpawnCatalog.Kind)r.U8();
            string id = r.Str();
            string json = r.Str();
            if (!ClientGate.PuppetActive) return;
            Sanctioned.Add(id);
            if (ContactRegistry.TryGetContact(id, out var existing) && existing is MonoBehaviour mb && mb != null) return;
            try
            {
                bool ok = ClientGate.AllowLocalAction(() => kind == SpawnCatalog.Kind.Clone
                    ? SpawnCatalog.ApplyClone(id, json)
                    : SpawnCatalog.Apply(kind, json));
                if (ok) SpawnsApplied++; else SpawnFailures++;
                CoopLog.Debug($"spawn {kind} {id}: {ok}");
            }
            catch (Exception e)
            {
                SpawnFailures++;
                CoopLog.Error($"Spawn {kind} {id} failed: {e}");
            }
        }

        private static void OnDespawn(NetReader r)
        {
            string id = r.Str();
            Sanctioned.Remove(id);
            if (!ContactRegistry.TryGetContact(id, out var c)) { DestroyByName(id); return; }
            var mb = c as MonoBehaviour;
            ClientGate.AllowLocalAction(() =>
            {
                ContactRegistry.Unregister(c);
                if (mb != null && mb.gameObject != null) UnityEngine.Object.Destroy(mb.gameObject);
            });
        }

        private static void DestroyByName(string id)
        {
            var go = GameObject.Find(id);
            if (go != null && go.GetComponent<IContact>() != null) ClientGate.AllowLocalAction(() => UnityEngine.Object.Destroy(go));
        }

        private static void OnStates(NetReader r)
        {
            if (!ClientGate.PuppetActive) return;
            int n = r.U16();
            for (int i = 0; i < n; i++)
            {
                string id = r.Str();
                Vector3 pos = r.Vec3();
                Vector3 vel = new Vector3(r.F32(), r.F32(), 0f);
                float rotZ = r.U16() * (360f / 65535f);
                if (!ContactRegistry.TryGetContact(id, out var c)) continue;
                var mb = c as MonoBehaviour;
                if (mb == null || mb.Equals(null)) continue;
                var p = mb.GetComponent<CoopPuppet>() ?? mb.gameObject.AddComponent<CoopPuppet>();
                p.Push(pos, vel, rotZ);
            }
        }

        /// <summary>Client: a contact registered. Anything the host did not sanction is a local leak.</summary>
        internal static void ClientOnRegister(IContact c)
        {
            if (!_adopted || !ClientGate.Suppress || c == null || IsLocalAuthority(c)) return;
            string id = SafeId(c);
            if (id == null || Sanctioned.Contains(id)) return;
            var mb = c as MonoBehaviour;
            if (mb == null) return;
            RogueSpawns++;
            CoopLog.Warn($"Local spawn not from host: {id} ({c.GetType().Name}) - removing\n{Environment.StackTrace}");
            RogueToDestroy.Add(mb.gameObject);
        }

        public static void ClientTick()
        {
            if (_adoptAt > 0f && Time.realtimeSinceStartup >= _adoptAt && ClientGate.PuppetActive)
            {
                _adoptAt = -1f;
                ClientAdoptLoadedWorld();
            }
            if (RogueToDestroy.Count == 0) return;
            ClientGate.AllowLocalAction(() =>
            {
                foreach (var go in RogueToDestroy)
                {
                    if (go == null) continue;
                    var c = go.GetComponent<IContact>();
                    if (c != null) ContactRegistry.Unregister(c);
                    UnityEngine.Object.Destroy(go);
                }
            });
            RogueToDestroy.Clear();
        }
    }
}
