using System;
using System.Collections.Generic;
using AirDefender;
using AirDefenderCoop.Net;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Game state with shapes the field codec cannot carry (nested collections, structs, static
    /// readonly collections), synchronised as JSON whenever it changes.
    /// </summary>
    internal static class CustomGlobals
    {
        [Serializable]
        private sealed class DeltaList { public List<Bootstrapper.VirtualDeltaEntry> items = new List<Bootstrapper.VirtualDeltaEntry>(); }

        [Serializable]
        private sealed class ChaffList { public List<int> index = new List<int>(); public List<Vector3> origin = new List<Vector3>(); public List<float> born = new List<float>(); }

        // Chaff clouds are released by the host's (gated) interceptors and missiles. Each side's
        // radar paints them from ChaffCloudRegistry, so the client mirrors the host's slots.
        private static string CaptureChaff()
        {
            var l = new ChaffList();
            var clouds = ChaffCloudRegistry.s_clouds;
            for (int i = 0; i < clouds.Length; i++)
            {
                if (!clouds[i].active) continue;
                l.index.Add(i);
                l.origin.Add(clouds[i].origin);
                l.born.Add(clouds[i].bornGameSeconds);
            }
            return JsonUtility.ToJson(l);
        }

        private static void ApplyChaff(string json)
        {
            var l = JsonUtility.FromJson<ChaffList>(json);
            var clouds = ChaffCloudRegistry.s_clouds;
            var keep = new bool[clouds.Length];
            for (int k = 0; k < l.index.Count; k++)
            {
                int i = l.index[k];
                if (i < 0 || i >= clouds.Length) continue;
                keep[i] = true;
                if (clouds[i].active && clouds[i].origin == l.origin[k] && clouds[i].bornGameSeconds == l.born[k]) continue;
                if (clouds[i].active) ChaffCloudRegistry.Retire(i);
                clouds[i] = new ChaffCloudRegistry.Cloud { active = true, origin = l.origin[k], bornGameSeconds = l.born[k] };
            }
            for (int i = 0; i < clouds.Length; i++)
                if (!keep[i] && clouds[i].active) ChaffCloudRegistry.Retire(i);
        }

        private sealed class Entry
        {
            public string Key;
            public Func<string> Capture;
            public Action<string> Apply;
            public string LastSent;
            /// <summary>Client: re-apply the last value now and then, because local code also writes it.</summary>
            public bool Reapply;
            public string LastReceived;
            public bool CaptureFailed;   // warned once
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
            new Entry { Key = "ChaffClouds", Capture = CaptureChaff, Apply = ApplyChaff },
            new Entry { Key = "PendingQra", Capture = PanelMirrors.CapturePendingQra, Apply = PanelMirrors.ApplyPendingQra, Reapply = true },
            new Entry { Key = "HerculesFlights", Capture = PanelMirrors.CaptureHercules, Apply = PanelMirrors.ApplyHercules },
            new Entry { Key = "ChinookFlights", Capture = PanelMirrors.CaptureChinook, Apply = PanelMirrors.ApplyChinook },
            // Restored interceptors register channels locally on the client too.
            new Entry { Key = "RadioChannels", Capture = PanelMirrors.CaptureRadio, Apply = PanelMirrors.ApplyRadio, Reapply = true },
        };

        private static float _next;
        private static float _nextReapply;

        public static void Init()
        {
            CoopSession.Register(MsgType.CustomGlobal, OnGlobal);
            Bootstrap.WorldSync.PartnerContactsReported += (_, __) => { foreach (var e in Entries) e.LastSent = null; };
            Bootstrap.WorldSync.ClientWorldLoaded += () => { foreach (var e in Entries) e.LastReceived = null; };
        }

        public static void ClientTick()
        {
            if (!ClientGate.PuppetActive) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextReapply) return;
            _nextReapply = now + 2f;
            foreach (var e in Entries)
                if (e.Reapply && e.LastReceived != null) ApplyEntry(e, e.LastReceived);
        }

        private static void ApplyEntry(Entry e, string json)
        {
            try { ClientGate.AllowLocalAction(() => e.Apply(json)); }
            catch (Exception ex) { CoopLog.Warn($"apply {e.Key}: {ex.Message}"); }
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
                catch (Exception ex)
                {
                    if (!e.CaptureFailed) CoopLog.Warn($"capture {e.Key}: {ex}");
                    e.CaptureFailed = true;
                    continue;
                }
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
                e.LastReceived = json;
                ApplyEntry(e, json);
            }
        }
    }
}
