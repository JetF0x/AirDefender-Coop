using System;
using System.Collections.Generic;
using AirDefender;
using AirDefenderCoop.Net;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Radar tracks are built locally on each side from the (replicated) aircraft, so they move with
    /// the local sweep. Everything a player reads off a track label - serial number, identity and
    /// colour, role, callsign - is decided by the host and enforced here.
    /// </summary>
    public static class TrackSync
    {
        private const float HostInterval = 0.5f;
        private const float ClientInterval = 0.25f;

        private struct Info
        {
            public int Serial;
            public string Ident;
            public Color Color;
            public string Role;
            public string Callsign;
            public string Spoken;

            public ulong Hash()
            {
                unchecked
                {
                    ulong h = 1469598103934665603UL;
                    void Mix(string s) { if (s != null) foreach (char c in s) { h ^= c; h *= 1099511628211UL; } h ^= 0xff; h *= 1099511628211UL; }
                    h ^= (ulong)Serial; h *= 1099511628211UL;
                    Mix(Ident); Mix(Role); Mix(Callsign); Mix(Spoken);
                    h ^= (ulong)Mathf.RoundToInt(Color.r * 255) | ((ulong)Mathf.RoundToInt(Color.g * 255) << 8) | ((ulong)Mathf.RoundToInt(Color.b * 255) << 16);
                    h *= 1099511628211UL;
                    return h;
                }
            }
        }

        private static readonly Dictionary<string, ulong> HostSent = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Info> ClientTable = new Dictionary<string, Info>(StringComparer.Ordinal);
        private static float _next;
        public static int Corrections { get; private set; }

        public static void Init()
        {
            CoopSession.Register(MsgType.TrackTable, OnTable);
            Bootstrap.WorldSync.PartnerContactsReported += _ => HostSent.Clear();
            CoopSession.PartnerLeft += _ => { HostSent.Clear(); ClientTable.Clear(); };
            Bootstrap.WorldSync.ClientWorldLoaded += () => ClientTable.Clear();
        }

        private static bool TryRead(TrackManager tm, string id, out Info info)
        {
            info = default;
            if (!tm.contactToTrack.TryGetValue(id, out var t) || t == null || t.renderer == null) return false;
            var r = t.renderer;
            info = new Info
            {
                Serial = t.serialId,
                Ident = r.identifier,
                Color = r.color,
                Role = t.roleCode,
                Callsign = r.callsign,
                Spoken = r.spokenCallsign,
            };
            return true;
        }

        // ================================================================ host

        public static void HostTick()
        {
            if (!Bootstrap.WorldSync.PartnerWorldReady) return;
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + HostInterval;
            var tm = TrackManager.Instance;
            if (tm == null) return;

            var changed = new List<(string id, Info info)>();
            foreach (var kv in tm.contactToTrack)
            {
                if (!TryRead(tm, kv.Key, out var info)) continue;
                ulong h = info.Hash();
                if (HostSent.TryGetValue(kv.Key, out var old) && old == h) continue;
                HostSent[kv.Key] = h;
                changed.Add((kv.Key, info));
            }
            if (changed.Count == 0) return;
            for (int start = 0; start < changed.Count; start += 64)
            {
                int end = Math.Min(changed.Count, start + 64);
                int s0 = start;
                CoopSession.Send(MsgType.TrackTable, w =>
                {
                    w.U16((ushort)(end - s0));
                    for (int i = s0; i < end; i++)
                    {
                        var (id, info) = changed[i];
                        w.Str(id);
                        w.I32(info.Serial);
                        w.Str(info.Ident);
                        w.Color(info.Color);
                        w.Str(info.Role);
                        w.Str(info.Callsign);
                        w.Str(info.Spoken);
                    }
                    w.I32(tm.nextSerialId);
                });
            }
        }

        // ================================================================ client

        private static void OnTable(NetReader r)
        {
            int n = r.U16();
            for (int i = 0; i < n; i++)
            {
                string id = r.Str();
                ClientTable[id] = new Info
                {
                    Serial = r.I32(), Ident = r.Str(), Color = r.Color(), Role = r.Str(), Callsign = r.Str(), Spoken = r.Str()
                };
            }
            int nextSerial = r.I32();
            var tm = TrackManager.Instance;
            if (tm != null && ClientGate.PuppetActive) tm.nextSerialId = Math.Max(tm.nextSerialId, nextSerial);
            _nextClient = 0f;
        }

        private static float _nextClient;

        public static void ClientTick()
        {
            if (!ClientGate.PuppetActive) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextClient) return;
            _nextClient = now + ClientInterval;
            var tm = TrackManager.Instance;
            if (tm == null) return;

            ClientGate.AllowLocalAction(() =>
            {
                foreach (var kv in tm.contactToTrack)
                {
                    if (!ClientTable.TryGetValue(kv.Key, out var want)) continue;
                    var t = kv.Value;
                    var rend = t?.renderer;
                    if (rend == null) continue;
                    if (t.serialId != want.Serial && want.Serial > 0)
                    {
                        t.serialId = want.Serial;
                        tm.contactIdToSerial[kv.Key] = want.Serial;
                        rend.SetTrackId(want.Serial);
                        Corrections++;
                    }
                    if (rend.identifier != want.Ident || rend.color != want.Color)
                    {
                        rend.SetIdentifierAndColor(want.Ident, want.Color);
                        Corrections++;
                    }
                    if (t.roleCode != want.Role)
                    {
                        t.roleCode = want.Role;
                        rend.SetRole(want.Role);
                    }
                    if (rend.callsign != want.Callsign && want.Callsign != null) rend.SetCallsign(want.Callsign);
                    if (rend.spokenCallsign != want.Spoken && want.Spoken != null) rend.SetSpokenCallsign(want.Spoken);
                }
            });
        }
    }
}
