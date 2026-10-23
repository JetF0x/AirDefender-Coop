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

        private enum Kind : byte { Asma = 1, Voice = 2, Alert = 3, Sfx = 4, Nuke = 5, Overlay = 6, Banner = 7, Music = 8 }

        private static readonly Dictionary<int, string> ClipPaths = new Dictionary<int, string>();
        private static readonly Dictionary<string, float> RecentLocal = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> RecentRemote = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<object, object> Forwarded = new ConditionalWeakTable<object, object>();
        [ThreadStatic] private static bool _applyingRemote;

        public static int AsmaForwarded { get; private set; }
        public static int VoiceForwarded { get; private set; }
        public static int Deduped { get; private set; }
        public static int SfxApplied { get; private set; }
        public static int NukesApplied { get; private set; }
        public static int BannersApplied { get; private set; }
        public static int MusicApplied { get; private set; }

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
            h.Patch(AccessTools.Method(typeof(SfxServiceImpl), nameof(SfxServiceImpl.PlayInternal), new[] { typeof(AudioClip), typeof(float) }),
                prefix: new HarmonyMethod(typeof(PresentationSync), nameof(BeforeSfx)));
            h.Patch(AccessTools.Method(typeof(SfxServiceImpl), nameof(SfxServiceImpl.PlayInternalNormalized), new[] { typeof(AudioClip), typeof(AudioCategory), typeof(float) }),
                prefix: new HarmonyMethod(typeof(PresentationSync), nameof(BeforeSfxNormalized)));
            h.Patch(AccessTools.Method(typeof(NuclearBlastEffect), nameof(NuclearBlastEffect.Spawn), new[] { typeof(Vector3), typeof(float), typeof(NuclearDamageBands?), typeof(float), typeof(float) }),
                postfix: new HarmonyMethod(typeof(PresentationSync), nameof(AfterNuke)));
            h.Patch(AccessTools.Method(typeof(AlertOverlay), nameof(AlertOverlay.Show), new[] { typeof(string), typeof(float) }),
                prefix: new HarmonyMethod(typeof(PresentationSync), nameof(BeforeOverlay)));
            h.Patch(AccessTools.Method(typeof(AlertOverlay), nameof(AlertOverlay.Show), new[] { typeof(string), typeof(float), typeof(Color) }),
                prefix: new HarmonyMethod(typeof(PresentationSync), nameof(BeforeOverlayColor)));
            h.Patch(AccessTools.Method(typeof(SessionBannerUI), nameof(SessionBannerUI.ShowHudNotification), new[] { typeof(string), typeof(float) }),
                prefix: new HarmonyMethod(typeof(PresentationSync), nameof(BeforeBanner)));
            h.Patch(AccessTools.Method(typeof(SessionBannerUI), nameof(SessionBannerUI.ShowHudNotification), new[] { typeof(string), typeof(float), typeof(Color) }),
                prefix: new HarmonyMethod(typeof(PresentationSync), nameof(BeforeBannerColor)));
            h.Patch(AccessTools.Method(typeof(GameplayMusicCues), nameof(GameplayMusicCues.NotifyFriendlyShotDown)),
                postfix: new HarmonyMethod(typeof(PresentationSync), nameof(AfterFriendlyLossMusic)));
            h.Patch(AccessTools.Method(typeof(GameplayMusicCues), nameof(GameplayMusicCues.NotifyEasPlayed)),
                postfix: new HarmonyMethod(typeof(PresentationSync), nameof(AfterEasMusic)));
        }

        private static bool HostForwarding => CoopSession.IsHost && CoopSession.Connected && Bootstrap.WorldSync.PartnerWorldReady;

        /// <summary>
        /// Host: whether presentation being produced right now belongs to the clients. Produced by
        /// the host's simulation it goes to everyone; produced while running a client's command it
        /// goes back to that client only. Anything else is the host player's own UI.
        /// </summary>
        private static bool HostShouldForward(out bool toSender)
        {
            toSender = false;
            if (!HostForwarding || _applyingRemote) return false;
            if (Commands.CommandRouter.ExecutingRemote) { toSender = true; return true; }
            return Puppet.SimulationGate.HostSimDepth > 0;
        }

        private static void HostSend(bool toSender, Action<NetWriter> body)
        {
            if (toSender) CoopSession.Reply(MsgType.GameEvent, body);
            else CoopSession.Send(MsgType.GameEvent, body);
        }

        /// <summary>Client: a locally produced copy of something the host just sent is dropped, and vice versa.</summary>
        private static bool ClientKeepLocal(string key)
        {
            if (!CoopSession.IsClient || !ClientGate.PuppetActive || _applyingRemote) return true;
            if (Seen(RecentRemote, key)) { Deduped++; return false; }
            Mark(RecentLocal, key);
            return true;
        }

        private static bool ClientTakeRemote(string key)
        {
            if (Seen(RecentLocal, key)) { Deduped++; return false; }
            Mark(RecentRemote, key);
            return true;
        }

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

        // ---------------------------------------------------------------- simulation sounds

        // One-shot sounds (explosions, missile warnings, score awards and penalties, launch audio)
        // go through these two methods. The host forwards the ones its simulation plays.
        private static bool BeforeSfx(AudioClip clip, float volume) => OnSfx(clip, volume, -1);

        private static bool BeforeSfxNormalized(AudioClip clip, AudioCategory category, float baseVolume) => OnSfx(clip, baseVolume, (int)category);

        private static bool OnSfx(AudioClip clip, float volume, int category)
        {
            string path = PathOf(clip);
            if (path == null) return true;
            string key = "sfx|" + path;
            if (CoopSession.IsClient) return ClientKeepLocal(key);
            if (HostShouldForward(out bool toSender))
                HostSend(toSender, w => { w.U8((byte)Kind.Sfx); w.Str(path); w.F32(volume); w.I32(category); });
            return true;
        }

        private static void ApplySfx(NetReader r)
        {
            string path = r.Str();
            float volume = r.F32();
            int category = r.I32();
            if (!ClientTakeRemote("sfx|" + path)) return;
            var clip = Resources.Load<AudioClip>(path);
            var sfx = UnityEngine.Object.FindAnyObjectByType<SfxServiceImpl>();
            if (clip == null || sfx == null) return;
            SfxApplied++;
            _applyingRemote = true;
            try
            {
                ClientGate.AllowLocalAction(() =>
                {
                    if (category < 0) sfx.PlayInternal(clip, volume);
                    else sfx.PlayInternalNormalized(clip, (AudioCategory)category, volume);
                });
            }
            finally { _applyingRemote = false; }
        }

        // ---------------------------------------------------------------- nuclear blasts

        // The blast effect plays its own sounds and screen flicker. Those sounds reach the client
        // first (forwarded from inside Spawn), so its local copies are de-duplicated.
        private static void AfterNuke(Vector3 worldPos, float radiusMiles, NuclearDamageBands? damageBands, float bloomMiles, float dissipateMpm)
        {
            if (!HostForwarding || _applyingRemote) return;
            CoopSession.Send(MsgType.GameEvent, w =>
            {
                w.U8((byte)Kind.Nuke);
                w.Vec3(worldPos);
                w.F32(radiusMiles);
                w.Bool(damageBands.HasValue);
                var b = damageBands.GetValueOrDefault();
                w.F32(b.FireballRadiusMiles); w.F32(b.ModerateBlastRadiusMiles); w.F32(b.ThermalRadiusMiles); w.F32(b.LightBlastRadiusMiles);
                w.F32(bloomMiles);
                w.F32(dissipateMpm);
            });
        }

        private static void ApplyNuke(NetReader r)
        {
            Vector3 pos = r.Vec3();
            float radius = r.F32();
            bool hasBands = r.Bool();
            var bands = new NuclearDamageBands { FireballRadiusMiles = r.F32(), ModerateBlastRadiusMiles = r.F32(), ThermalRadiusMiles = r.F32(), LightBlastRadiusMiles = r.F32() };
            float bloom = r.F32();
            float dissipate = r.F32();
            NukesApplied++;
            ClientGate.AllowLocalAction(() => NuclearBlastEffect.Spawn(pos, radius, hasBands ? bands : (NuclearDamageBands?)null, bloom, dissipate));
        }

        // ---------------------------------------------------------------- alert overlay and HUD banner

        private static bool BeforeOverlay(string message, float seconds) => OnBanner(Kind.Overlay, message, seconds, null);
        private static bool BeforeOverlayColor(string message, float seconds, Color color) => OnBanner(Kind.Overlay, message, seconds, color);
        private static bool BeforeBanner(string message, float seconds) => OnBanner(Kind.Banner, message, seconds, null);
        private static bool BeforeBannerColor(string message, float seconds, Color color) => OnBanner(Kind.Banner, message, seconds, color);

        private static bool OnBanner(Kind kind, string message, float seconds, Color? color)
        {
            if (string.IsNullOrEmpty(message)) return true;
            string key = (kind == Kind.Overlay ? "overlay|" : "banner|") + message;
            if (CoopSession.IsClient) return ClientKeepLocal(key);
            if (HostShouldForward(out bool toSender))
                HostSend(toSender, w => { w.U8((byte)kind); w.Str(message); w.F32(seconds); w.Bool(color.HasValue); w.Color(color ?? Color.white); });
            return true;
        }

        private static void ApplyBanner(Kind kind, NetReader r)
        {
            string message = r.Str();
            float seconds = r.F32();
            bool hasColor = r.Bool();
            Color color = r.Color();
            if (!ClientTakeRemote((kind == Kind.Overlay ? "overlay|" : "banner|") + message)) return;
            BannersApplied++;
            _applyingRemote = true;
            try
            {
                ClientGate.AllowLocalAction(() =>
                {
                    if (kind == Kind.Overlay)
                    {
                        if (hasColor) AlertOverlay.Show(message, seconds, color); else AlertOverlay.Show(message, seconds);
                    }
                    else if (hasColor) SessionBannerUI.ShowHudNotification(message, seconds, color);
                    else SessionBannerUI.ShowHudNotification(message, seconds);
                });
            }
            finally { _applyingRemote = false; }
        }

        // ---------------------------------------------------------------- music cues

        // GameplayMusicCues runs on every peer; only the host's simulation tells it what happened.
        private enum Cue : byte { FriendlyLoss = 1, Eas = 2 }

        private static void AfterFriendlyLossMusic() => SendCue(Cue.FriendlyLoss, 0f);
        private static void AfterEasMusic(float easClipSeconds) => SendCue(Cue.Eas, easClipSeconds);

        private static void SendCue(Cue cue, float seconds)
        {
            if (!HostForwarding || _applyingRemote) return;
            CoopSession.Send(MsgType.GameEvent, w => { w.U8((byte)Kind.Music); w.U8((byte)cue); w.F32(seconds); });
        }

        private static void ApplyMusic(NetReader r)
        {
            var cue = (Cue)r.U8();
            float seconds = r.F32();
            MusicApplied++;
            _applyingRemote = true;
            try
            {
                ClientGate.AllowLocalAction(() =>
                {
                    if (cue == Cue.FriendlyLoss) GameplayMusicCues.NotifyFriendlyShotDown();
                    else if (cue == Cue.Eas) GameplayMusicCues.NotifyEasPlayed(seconds);
                });
            }
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
                case Kind.Sfx: ApplySfx(r); break;
                case Kind.Nuke: ApplyNuke(r); break;
                case Kind.Overlay:
                case Kind.Banner: ApplyBanner(kind, r); break;
                case Kind.Music: ApplyMusic(r); break;
            }
        }
    }
}
