using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using AirDefender;
using UnityEngine;

namespace AirDefenderCoop.Diagnostics
{
    /// <summary>Developer dumps of the live world (F10) used to map what replication must cover.</summary>
    public static class DevDump
    {
        public static void All()
        {
            Contacts();
            Behaviours();
            SnapshotCost();
        }

        public static void Contacts()
        {
            var sb = new StringBuilder();
            var contacts = ContactRegistry.GetAllContacts();
            sb.AppendLine($"[DUMP] {contacts.Length} contacts");
            var byShape = new Dictionary<string, int>();
            foreach (var c in contacts)
            {
                var mb = c as MonoBehaviour;
                if (mb == null) continue;
                var go = mb.gameObject;
                var comps = go.GetComponents<Component>().Where(x => x != null && !(x is Transform)).Select(x => x.GetType().Name);
                string shape = $"{c.GetType().Name} [{string.Join(",", comps)}] children={go.transform.childCount}";
                byShape.TryGetValue(shape, out int n);
                byShape[shape] = n + 1;
            }
            foreach (var kv in byShape.OrderByDescending(k => k.Value)) sb.AppendLine($"  {kv.Value,4} x {kv.Key}");
            int shown = 0;
            foreach (var c in contacts)
            {
                if (shown++ >= 40) break;
                var mb = c as MonoBehaviour;
                sb.AppendLine($"    {c.ContactId} go='{mb?.gameObject.name}' active={mb?.gameObject.activeInHierarchy} pos={mb?.transform.position}");
            }
            CoopLog.Info(sb.ToString());
        }

        public static void Behaviours()
        {
            var sb = new StringBuilder();
            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include);
            var contactGos = new HashSet<GameObject>(ContactRegistry.GetAllContacts().OfType<MonoBehaviour>().Select(m => m.gameObject));
            var groups = all.Where(b => b != null && b.GetType().Assembly == typeof(TrackManager).Assembly)
                .GroupBy(b => b.GetType().FullName)
                .Select(g => new
                {
                    Type = g.Key,
                    Count = g.Count(),
                    OnContacts = g.Count(b => contactGos.Contains(b.gameObject)),
                    Enabled = g.Count(b => b.isActiveAndEnabled)
                })
                .OrderByDescending(g => g.Count);
            sb.AppendLine($"[DUMP] game MonoBehaviours by type (count / on contact objects / active+enabled)");
            foreach (var g in groups) sb.AppendLine($"  {g.Count,5} {g.OnContacts,5} {g.Enabled,5}  {g.Type}");
            CoopLog.Info(sb.ToString());
        }

        public static void SnapshotCost()
        {
            if (!PlayerSession.IsLoggedIn()) return;
            var sw = Stopwatch.StartNew();
            var blob = GameSaveManager.BuildSnapshot();
            long build = sw.ElapsedMilliseconds;
            string json = JsonUtility.ToJson(blob);
            CoopLog.Info($"[DUMP] snapshot build {build} ms, json {sw.ElapsedMilliseconds - build} ms, {json.Length} chars; " +
                         $"civil={blob.civilianAircraft?.Count} vfr={blob.vfrAircraft?.Count} enemies={blob.activeEnemies?.Count} " +
                         $"interceptors={blob.activeInterceptors?.Count} parked={blob.parkedAssets?.Count} stationed={blob.stationedMilitary?.Count} " +
                         $"missiles={blob.inFlightMissiles?.Count} tracks={blob.trackStates?.Count} timeScale={Time.timeScale}");
        }
    }
}
