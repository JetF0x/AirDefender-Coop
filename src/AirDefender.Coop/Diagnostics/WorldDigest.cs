using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AirDefender;
using AirDefenderCoop.Net;
using AirDefenderCoop.Replication;
using UnityEngine;

namespace AirDefenderCoop.Diagnostics
{
    /// <summary>
    /// Every few seconds the host sends a compact description of its world (contacts and positions,
    /// tracks with serial/identity, scores, clock). The client compares it with its own world and
    /// logs every difference. This is the main tool for proving and debugging synchronisation.
    /// </summary>
    public static class WorldDigest
    {
        private const float Interval = 5f;
        private const float PosTolerance = 3f; // world units (~1.5 nm)

        private static float _next;
        private static long _lastBytes;
        private static float _lastStatsTime;

        public static int Checks { get; private set; }
        public static int CleanChecks { get; private set; }
        public static string LastSummary { get; private set; } = "";

        public static void Init()
        {
            CoopSession.Register(MsgType.Digest, OnDigest);
        }

        public static void HostTick()
        {
            if (!Bootstrap.WorldSync.PartnerWorldReady) return;
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            float window = _lastStatsTime > 0 ? now - _lastStatsTime : Interval;
            CoopLog.Info($"[STATS] contacts={EntityReplicator.HostTracked} spawnsSent={EntityReplicator.SpawnsSent} fieldsSent={StateReplicator.FieldsSent} " +
                         $"stateCost={StateReplicator.HostMsPerSecond:0.0}ms/s up={(CoopSession.BytesSent - _lastBytes) / 1024f / window:0.0}KB/s " +
                         $"players={CoopSession.Players.Count} inWorld={Bootstrap.WorldSync.ReadyPeerCount} rttMax={CoopSession.RttMs:0}ms cmds={Commands.CommandRouter.Executed}/{Commands.CommandRouter.Failed} asma={PresentationSync.AsmaForwarded} voice={PresentationSync.VoiceForwarded} hits={RadarHitSync.Forwarded}");
            _lastBytes = CoopSession.BytesSent;
            _lastStatsTime = now;
            _next = now + Interval;
            var contacts = Capture();
            var tracks = CaptureTracks();
            var scores = CaptureScores();
            double timeline = GameTimeline.IsAvailable() ? GameTimeline.GetTimelineSeconds() : -1;
            CoopSession.Send(MsgType.Digest, w =>
            {
                w.F64(timeline);
                w.F32(Time.timeScale);
                w.I32(contacts.Count);
                foreach (var kv in contacts) { w.Str(kv.Key); w.Vec3(kv.Value); }
                w.I32(tracks.Count);
                foreach (var kv in tracks) { w.Str(kv.Key); w.Str(kv.Value); }
                w.StrList(scores);
            });
        }

        private static Dictionary<string, Vector3> Capture()
        {
            var d = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            foreach (var c in ContactRegistry.GetAllContacts())
            {
                if (c == null || EntityReplicator.IsLocalAuthority(c)) continue;
                var mb = c as MonoBehaviour;
                if (mb == null || mb.Equals(null)) continue;
                string id;
                try { id = c.ContactId; } catch { continue; }
                if (!string.IsNullOrEmpty(id)) d[id] = mb.transform.position;
            }
            return d;
        }

        /// <summary>contactId -> "serial|identifier|stale".</summary>
        private static Dictionary<string, string> CaptureTracks()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var tm = UnityEngine.Object.FindAnyObjectByType<TrackManager>();
                if (tm == null) return d;
                TrackManager.ForEachActiveTrack((id, track) =>
                {
                    if (string.IsNullOrEmpty(id)) return;
                    int serial = tm.GetSerialForContact(id);
                    string ident = tm.GetIdentifierForContact(id) ?? "";
                    d[id] = $"{serial}|{ident}";
                });
            }
            catch (Exception e) { CoopLog.Debug("digest tracks: " + e.Message); }
            return d;
        }

        private static List<string> CaptureScores()
        {
            var l = new List<string>();
            try
            {
                l.Add("tension=" + TriStateScoringService.GetTensionScore());
                l.Add("performance=" + TriStateScoringService.GetPerformanceScore());
                l.Add("public=" + TriStateScoringService.GetPublicScore());
                // Base inventory: remaining F3s per base.
                Bootstrapper.EnsureManifestEntriesCache();
                var bases = new List<string>(Bootstrapper.s_manifestEntriesByBase.Keys);
                bases.Sort(StringComparer.OrdinalIgnoreCase);
                var inv = new StringBuilder("F3:");
                foreach (var b in bases)
                {
                    int n = Bootstrapper.GetRemainingCountForBaseAndType(b, "F3");
                    if (n > 0) inv.Append(b).Append('=').Append(n).Append(' ');
                }
                l.Add(inv.ToString());
            }
            catch (Exception e) { l.Add("scores unavailable: " + e.Message); }
            return l;
        }

        private static void OnDigest(NetReader r)
        {
            double hostTimeline = r.F64();
            float hostScale = r.F32();
            int n = r.I32();
            var host = new Dictionary<string, Vector3>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++) host[r.Str()] = r.Vec3();
            int tn = r.I32();
            var hostTracks = new Dictionary<string, string>(tn, StringComparer.Ordinal);
            for (int i = 0; i < tn; i++) hostTracks[r.Str()] = r.Str();
            var hostScores = r.StrList();
            if (!ClientGate.PuppetActive) return;

            var mine = Capture();
            var missing = host.Keys.Where(k => !mine.ContainsKey(k)).ToList();
            var extra = mine.Keys.Where(k => !host.ContainsKey(k)).ToList();
            float maxErr = 0f;
            string worst = null;
            int far = 0;
            foreach (var kv in host)
            {
                if (!mine.TryGetValue(kv.Key, out var p)) continue;
                float e = Vector2.Distance(p, kv.Value);
                if (e > maxErr) { maxErr = e; worst = kv.Key; }
                if (e > PosTolerance) far++;
            }

            var myTracks = CaptureTracks();
            int trackMissing = 0, trackExtra = 0, trackDiff = 0;
            var diffs = new List<string>();
            foreach (var kv in hostTracks)
            {
                if (!myTracks.TryGetValue(kv.Key, out var v)) { trackMissing++; continue; }
                if (v != kv.Value) { trackDiff++; if (diffs.Count < 6) diffs.Add($"{kv.Key}: host {kv.Value} / mine {v}"); }
            }
            foreach (var k in myTracks.Keys) if (!hostTracks.ContainsKey(k)) trackExtra++;
            var myScores = CaptureScores();
            bool scoresMatch = hostScores != null && hostScores.SequenceEqual(myScores);
            double clockDiff = GameTimeline.IsAvailable() ? GameTimeline.GetTimelineSeconds() - hostTimeline : 0;

            Checks++;
            bool clean = missing.Count == 0 && extra.Count == 0 && far == 0 && trackDiff == 0 && scoresMatch && Math.Abs(hostScale - Time.timeScale) < 1e-3f;
            if (clean) CleanChecks++;

            var sb = new StringBuilder();
            sb.Append($"[DIGEST] {(clean ? "OK" : "DIFF")} contacts host={host.Count} mine={mine.Count} missing={missing.Count} extra={extra.Count} ");
            sb.Append($"posErrMax={maxErr:0.0}({worst}) far={far} | tracks host={hostTracks.Count} mine={myTracks.Count} missing={trackMissing} extra={trackExtra} diff={trackDiff} ");
            sb.Append($"| scores {(scoresMatch ? "match" : "DIFFER host[" + string.Join(",", hostScores ?? new List<string>()) + "] mine[" + string.Join(",", myScores) + "]")} ");
            sb.Append($"| clock {clockDiff:+0.00;-0.00}s scale host={hostScale} mine={Time.timeScale} | applied fields={StateReplicator.FieldsApplied} spawns={EntityReplicator.SpawnsApplied}/{EntityReplicator.SpawnFailures} rogue={EntityReplicator.RogueSpawns} hits={RadarHitSync.Applied} deduped={PresentationSync.Deduped}");
            if (missing.Count > 0) sb.Append("\n   missing: " + string.Join(", ", missing.Take(10)));
            if (extra.Count > 0) sb.Append("\n   extra: " + string.Join(", ", extra.Take(10)));
            if (diffs.Count > 0) sb.Append("\n   track diffs: " + string.Join("; ", diffs));
            LastSummary = sb.ToString();
            CoopLog.Info(LastSummary);
        }
    }
}
