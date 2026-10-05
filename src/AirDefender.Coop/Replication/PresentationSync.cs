using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AirDefender;
using AirDefenderCoop.Commands;
using AirDefenderCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// What the host's simulation says and plays: ASMA console messages, radio voice and alert
    /// sounds. The client's own simulation is off, so these are forwarded. Anything both sides
    /// produce independently (UI feedback, traffic advisories from the shared picture) is
    /// de-duplicated by content within a short window.
    /// </summary>
    internal static class PresentationSync
    {
        private const float DedupeSeconds = 6f;

        private enum Kind : byte { Asma = 1, Voice = 2, Alert = 3 }

        private static readonly Dictionary<int, string> ClipPaths = new Dictionary<int, string>();
        private static readonly Dictionary<string, float> RecentLocal = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> RecentRemote = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<object, object> Forwarded = new ConditionalWeakTable<object, object>();
        [ThreadStatic] private static bool _applyingRemote;

        public static int AsmaForwarded { get; private set; }
        public static int VoiceForwarded { get; private set; }
        public static int Deduped { get; private set; }

        public static void Init()
        {
            CoopSession.Register(MsgType.GameEvent, OnEvent);
            CommandRouter.Register("AsmaOption", HostAsmaOption);
        }

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Resources), nameof(Resources.Load), new[] { typeof(string), typeof(Type) }),
                postfix: new HarmonyMethod(typeof(PresentationSync), nameof(AfterResourcesLoad)));
            h.Patch(AccessTools.Method(typeof(AsmaServiceImpl), nameof(AsmaServiceImpl.StartPostNow)),
                prefix: new HarmonyMethod(typeof(PresentationSync), nameof(BeforeAsmaPost)),
                postfix: new HarmonyMethod(typeof(PresentationSync), nameof(AfterAsmaPost)));
            h.Patch(AccessTools.Method(typeof(VoiceAudioQueue), nameof(VoiceAudioQueue.EvaluateShouldPlay)),
                postfix: new HarmonyMethod(typeof(PresentationSync), nameof(AfterVoiceGate)));
            h.Patch(AccessTools.Method(typeof(SfxServiceImpl), nameof(SfxServiceImpl.PlayAlertNonVoice)),
                prefix: new HarmonyMethod(typeof(PresentationSync), nameof(BeforeAlert)));
        }

        private static bool HostForwarding => CoopSession.IsHost && CoopSession.Connected && Bootstrap.WorldSync.PartnerWorldReady;

        private static bool Seen(Dictionary<string, float> d, string key)
        {
            return d.TryGetValue(key, out float t) && Time.realtimeSinceStartup - t < DedupeSeconds;
        }

        private static void Mark(Dictionary<string, float> d, string key)
        {
            d[key] = Time.realtimeSinceStartup;
            if (d.Count > 512)
            {
                var stale = new List<string>();
                foreach (var kv in d) if (Time.realtimeSinceStartup - kv.Value > DedupeSeconds) stale.Add(kv.Key);
                foreach (var k in stale) d.Remove(k);
            }
        }

        // ---------------------------------------------------------------- clip identity

        private static void AfterResourcesLoad(string path, UnityEngine.Object __result)
        {
            if (__result is AudioClip clip) ClipPaths[clip.GetInstanceID()] = path;
        }

        private static string PathOf(AudioClip clip) =>
            clip != null && ClipPaths.TryGetValue(clip.GetInstanceID(), out var p) ? p : null;

        // ---------------------------------------------------------------- ASMA

        private static string AsmaKey(AsmaMessage m) => "asma|" + m.header + "|" + m.body;

        private static bool BeforeAsmaPost(AsmaMessage m)
        {
            if (m == null || !CoopSession.IsClient || !ClientGate.PuppetActive || _applyingRemote) return true;
            if (m.category == AsmaMessageCategory.Coaching) return true; // personal mentor tips
            string key = AsmaKey(m);
            if (Seen(RecentRemote, key)) { Deduped++; return false; }
            Mark(RecentLocal, key);
            return true;
        }

        private static void AfterAsmaPost(AsmaMessage m)
        {
            if (m == null || !HostForwarding || m.category == AsmaMessageCategory.Coaching || m.devOnly) return;
            AsmaForwarded++;
            CoopSession.Send(MsgType.GameEvent, w =>
            {
                w.U8((byte)Kind.Asma);
                w.Str(m.messageId);
                w.I64(m.simUtc.Ticks);
                w.Str(m.source);
                w.Str(m.header);
                w.Str(m.body);
                w.U8((byte)m.category);
                w.U8((byte)m.severity);
                w.Bool(m.requiresAcknowledge);
                w.Bool(m.teletype);
                w.U16((ushort)m.options.Count);
                foreach (var o in m.options) { w.Str(o.label); w.I32((int)o.hotkey); w.Bool(o.isDefault); }
            });
        }

        private static void ApplyAsma(NetReader r)
        {
            var m = new AsmaMessage
            {
                messageId = r.Str(),
                simUtc = new DateTime(r.I64(), DateTimeKind.Utc),
                source = r.Str(),
                header = r.Str(),
                body = r.Str(),
                category = (AsmaMessageCategory)r.U8(),
                severity = (AsmaMessageSeverity)r.U8(),
                requiresAcknowledge = r.Bool(),
                teletype = r.Bool(),
            };
            int n = r.U16();
            string hostId = m.messageId;
            for (int i = 0; i < n; i++)
            {
                int index = i;
                var o = new AsmaOption { label = r.Str(), hotkey = (KeyCode)r.I32(), isDefault = r.Bool() };
                o.onSelected = () => CommandRouter.Route("AsmaOption", w => { w.Str(hostId); w.I32(index); });
                m.options.Add(o);
            }
            string key = AsmaKey(m);
            if (Seen(RecentLocal, key)) { Deduped++; return; }
            Mark(RecentRemote, key);
            _applyingRemote = true;
            try { ClientGate.AllowLocalAction(() => AsmaService.Post(m)); }
            finally { _applyingRemote = false; }
        }

        /// <summary>Host: the partner pressed a button on one of our ASMA messages.</summary>
        private static void HostAsmaOption(NetReader r)
        {
            string id = r.Str();
            int index = r.I32();
            var impl = AsmaService._impl as AsmaServiceImpl;
            if (impl == null) return;
            foreach (var m in impl.messages)
            {
                if (m == null || m.messageId != id) continue;
                if (index >= 0 && index < m.options.Count) m.options[index].onSelected?.Invoke();
                return;
            }
        }

        // ---------------------------------------------------------------- voice

        private static void AfterVoiceGate(VoiceAudioQueue.VoiceAudioRequest request, bool __result)
        {
            if (!__result || request == null || request.clip == null) return;
            string path = PathOf(request.clip);
            if (path == null) return;

            if (CoopSession.IsClient)
            {
                // A clip the host just sent is our own playback of it, not a local duplicate.
                string key = "voice|" + path;
                if (!Seen(RecentRemote, key)) Mark(RecentLocal, key);
                return;
            }
            if (!HostForwarding || Forwarded.TryGetValue(request, out _)) return;
            Forwarded.Add(request, null);
            VoiceForwarded++;
            CoopSession.Send(MsgType.GameEvent, w => { w.U8((byte)Kind.Voice); w.Str(path); w.F32(request.volume); w.Bool(request.isNormalized); });
        }

        private static void ApplyVoice(NetReader r)
        {
            string path = r.Str();
            float volume = r.F32();
            bool normalized = r.Bool();
            string key = "voice|" + path;
            if (Seen(RecentLocal, key)) { Deduped++; return; }
            Mark(RecentRemote, key);
            var clip = Resources.Load<AudioClip>(path);
            if (clip == null) return;
            ClientGate.AllowLocalAction(() => VoiceAudioQueue.QueueGlobalVoiceClip(clip, volume, isNormalized: normalized));
        }

        // ---------------------------------------------------------------- alerts

        private static bool BeforeAlert(AudioClip clip, float baseVolume)
        {
            string path = PathOf(clip);
            if (path == null) return true;
            string key = "alert|" + path;
            if (CoopSession.IsClient && ClientGate.PuppetActive && !_applyingRemote)
            {
                if (Seen(RecentRemote, key)) { Deduped++; return false; }
                Mark(RecentLocal, key);
                return true;
            }
            if (HostForwarding)
                CoopSession.Send(MsgType.GameEvent, w => { w.U8((byte)Kind.Alert); w.Str(path); w.F32(baseVolume); });
            return true;
        }

        private static void ApplyAlert(NetReader r)
        {
            string path = r.Str();
            float volume = r.F32();
            string key = "alert|" + path;
            if (Seen(RecentLocal, key)) { Deduped++; return; }
            Mark(RecentRemote, key);
            var clip = Resources.Load<AudioClip>(path);
            var sfx = UnityEngine.Object.FindAnyObjectByType<SfxServiceImpl>();
            if (clip == null || sfx == null) return;
            _applyingRemote = true;
            try { ClientGate.AllowLocalAction(() => sfx.PlayAlertNonVoice(clip, volume)); }
            finally { _applyingRemote = false; }
        }

        // ---------------------------------------------------------------- dispatch

        private static void OnEvent(NetReader r)
        {
            var kind = (Kind)r.U8();
            if (!ClientGate.PuppetActive) return;
            switch (kind)
            {
                case Kind.Asma: ApplyAsma(r); break;
                case Kind.Voice: ApplyVoice(r); break;
                case Kind.Alert: ApplyAlert(r); break;
            }
        }
    }
}
