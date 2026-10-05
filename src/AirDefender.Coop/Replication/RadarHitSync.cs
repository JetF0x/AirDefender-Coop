using System.Collections.Generic;
using AirDefender;
using AirDefenderCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Ground radar sweeps run on both machines against the (replicated) aircraft, so tracks
    /// update in step with each player's own sweep line. Every other track source - AWACS Link 11,
    /// datalinks, missiles reporting themselves, jamming decoys, live traffic - is simulation the
    /// client does not run, so the host forwards those hits.
    /// </summary>
    internal static class RadarHitSync
    {
        private struct Hit
        {
            public string Id;
            public Vector3 Pos;
            public float Period;
            public int Station;
            public int Seq;
            public bool L11;
        }

        [System.ThreadStatic] private static int _groundDepth;
        [System.ThreadStatic] private static bool _applying;
        private static readonly List<Hit> Pending = new List<Hit>();
        private static float _nextFlush;

        public static int Forwarded { get; private set; }
        public static int Applied { get; private set; }

        public static void Init()
        {
            CoopSession.Register(MsgType.RadarHits, OnHits);
        }

        public static void Apply(Harmony h)
        {
            var rph = AccessTools.Method(typeof(RadarPrimaryHistory), "Update");
            h.Patch(rph, prefix: new HarmonyMethod(typeof(RadarHitSync), nameof(EnterGround)), finalizer: new HarmonyMethod(typeof(RadarHitSync), nameof(ExitGround)));
            h.Patch(AccessTools.Method(typeof(TrackManager), nameof(TrackManager.NotifyHit)),
                prefix: new HarmonyMethod(typeof(RadarHitSync), nameof(BeforeHit)));
        }

        private static void EnterGround() => _groundDepth++;
        private static System.Exception ExitGround(System.Exception __exception) { _groundDepth--; return __exception; }

        private static bool BeforeHit(string contactId, Vector3 worldPosition, float sweepPeriodSeconds, int stationId, int sweepSequence, bool isL11)
        {
            if (_groundDepth > 0 || _applying) return true;
            if (CoopSession.IsHost)
            {
                if (CoopSession.Connected && Bootstrap.WorldSync.PartnerWorldReady)
                    Pending.Add(new Hit { Id = contactId, Pos = worldPosition, Period = sweepPeriodSeconds, Station = stationId, Seq = sweepSequence, L11 = isL11 });
                return true;
            }
            // Client: non-ground hits only come from the host.
            return !ClientGate.Suppress;
        }

        public static void HostTick()
        {
            if (Pending.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextFlush && Pending.Count < 200) return;
            _nextFlush = now + 0.1f;
            int count = Pending.Count;
            var batch = Pending.ToArray();
            Pending.Clear();
            Forwarded += count;
            CoopSession.Send(MsgType.RadarHits, w =>
            {
                w.U16((ushort)count);
                foreach (var hit in batch)
                {
                    w.Str(hit.Id); w.Vec3(hit.Pos); w.F32(hit.Period); w.I32(hit.Station); w.I32(hit.Seq); w.Bool(hit.L11);
                }
            });
        }

        private static void OnHits(NetReader r)
        {
            int n = r.U16();
            var tm = TrackManager.Instance;
            bool apply = tm != null && ClientGate.PuppetActive;
            for (int i = 0; i < n; i++)
            {
                string id = r.Str(); Vector3 pos = r.Vec3(); float period = r.F32(); int station = r.I32(); int seq = r.I32(); bool l11 = r.Bool();
                if (!apply) continue;
                _applying = true;
                try { ClientGate.AllowLocalAction(() => tm.NotifyHit(id, pos, period, station, seq, l11)); Applied++; }
                catch (System.Exception e) { CoopLog.Debug("forwarded hit failed: " + e.Message); }
                finally { _applying = false; }
            }
        }
    }
}
