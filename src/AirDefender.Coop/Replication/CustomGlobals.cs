using System;
using System.Collections.Generic;
using AirDefender;
using AirDefenderCoop.Net;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Game state with shapes the field codec cannot carry (nested collections), synchronised
    /// through the game's own export/import functions as JSON whenever it changes.
    /// </summary>
    internal static class CustomGlobals
    {
        [Serializable]
        private sealed class DeltaList { public List<Bootstrapper.VirtualDeltaEntry> items = new List<Bootstrapper.VirtualDeltaEntry>(); }

        private sealed class Entry
        {
            public string Key;
            public Func<string> Capture;
            public Action<string> Apply;
            public string LastSent;
        }

        private static readonly List<Entry> Entries = new List<Entry>
        {
            new Entry
            {
                // Aircraft taken from / relocated between bases: drives every base inventory count.
                Key = "VirtualDeltas",
                Capture = () => JsonUtility.ToJson(new DeltaList { items = Bootstrapper.ExportVirtualDeltas() ?? new List<Bootstrapper.VirtualDeltaEntry>() }),
                Apply = json =>
                {
                    var d = JsonUtility.FromJson<DeltaList>(json);
                    Bootstrapper.ClearVirtualDeltas();
                    Bootstrapper.ImportVirtualDeltas(d.items);
                },
            },
        };

        private static float _next;

        public static void Init()
        {
            CoopSession.Register(MsgType.CustomGlobal, OnGlobal);
            Bootstrap.WorldSync.PartnerContactsReported += (_, __) => { foreach (var e in Entries) e.LastSent = null; };
        }

        public static void HostTick()
        {
            if (!Bootstrap.WorldSync.PartnerWorldReady) return;
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 1f;
            foreach (var e in Entries)
            {
                string json;
                try { json = e.Capture(); }
                catch (Exception ex) { CoopLog.Debug($"capture {e.Key}: {ex.Message}"); continue; }
                if (json == e.LastSent) continue;
                e.LastSent = json;
                CoopSession.Send(MsgType.CustomGlobal, w => { w.Str(e.Key); w.Str(json); });
            }
        }

        private static void OnGlobal(NetReader r)
        {
            string key = r.Str();
            string json = r.Str();
            if (!ClientGate.PuppetActive) return;
            foreach (var e in Entries)
            {
                if (e.Key != key) continue;
                try { ClientGate.AllowLocalAction(() => e.Apply(json)); }
                catch (Exception ex) { CoopLog.Warn($"apply {key}: {ex.Message}"); }
            }
        }
    }
}
