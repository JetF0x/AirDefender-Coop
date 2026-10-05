using System;
using AirDefender;
using AirDefenderCoop.Net;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// The host owns the clock: simulation speed (Time.timeScale, including pause) and the
    /// operational timeline. The client follows; its own speed buttons become requests to the host.
    /// </summary>
    public static class TimeSync
    {
        private const float Interval = 0.5f;
        private const double CorrectThreshold = 0.75; // timeline seconds

        private static float _next;
        private static float _lastSentScale = -1f;

        public static double LastDriftSeconds { get; private set; }

        public static void Init()
        {
            CoopSession.Register(MsgType.TimeSync, OnTimeSync);
        }

        public static void HostTick()
        {
            if (!WorldSyncReady) return;
            float now = Time.realtimeSinceStartup;
            bool scaleChanged = Math.Abs(Time.timeScale - _lastSentScale) > 1e-4f;
            if (!scaleChanged && now < _next) return;
            _next = now + Interval;
            _lastSentScale = Time.timeScale;
            double timeline = GameTimeline.IsAvailable() ? GameTimeline.GetTimelineSeconds() : -1.0;
            long startTicks = GameTimeline.IsAvailable() ? GameTimeline.GetStartDateUtc().Ticks : 0L;
            CoopSession.Send(MsgType.TimeSync, w =>
            {
                w.F32(Time.timeScale);
                w.F64(timeline);
                w.I64(startTicks);
            }, reliable: scaleChanged);
        }

        private static bool WorldSyncReady => Bootstrap.WorldSync.PartnerWorldReady;

        private static void OnTimeSync(NetReader r)
        {
            float scale = r.F32();
            double hostTimeline = r.F64();
            long startTicks = r.I64();
            if (!ClientGate.PuppetActive) return;

            if (Math.Abs(Time.timeScale - scale) > 1e-4f)
                ClientGate.AllowLocalAction(() => ApplyTimeScale(scale));

            if (hostTimeline >= 0 && GameTimeline.IsAvailable())
            {
                // Account for half the round trip at the current speed.
                double expected = hostTimeline + CoopSession.RttMs / 2000.0 * scale;
                double local = GameTimeline.GetTimelineSeconds();
                LastDriftSeconds = local - expected;
                if (Math.Abs(LastDriftSeconds) > CorrectThreshold || GameTimeline.GetStartDateUtc().Ticks != startTicks)
                {
                    var start = new DateTime(startTicks, DateTimeKind.Utc);
                    ClientGate.AllowLocalAction(() => GameTimeline.SetState(start, expected));
                }
            }
        }

        /// <summary>Applies a speed through the game's own control so audio pause/resume follows.</summary>
        public static void ApplyTimeScale(float scale)
        {
            var ui = UnityEngine.Object.FindAnyObjectByType<TimeControlsUI>();
            if (ui != null) ui.SetTimeScale(scale);
            // The UI clamps to its speed set; the shared clock must match exactly.
            if (Mathf.Abs(Time.timeScale - scale) > 1e-4f)
            {
                Time.timeScale = scale;
                Time.fixedDeltaTime = 0.02f * Mathf.Max(scale, 0f);
            }
        }
    }
}
