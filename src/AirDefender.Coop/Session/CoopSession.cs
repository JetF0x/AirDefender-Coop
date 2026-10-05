using System;
using System.Collections.Generic;
using AirDefenderCoop.Net;
using Steamworks;
using UnityEngine;

namespace AirDefenderCoop
{
    public enum CoopMode { Offline, Host, Client }

    public enum LinkState { Offline, Waiting, Handshaking, Connected }

    /// <summary>
    /// Owns the transport, the handshake, heartbeats and message dispatch. Everything runs on
    /// the Unity main thread via <see cref="Tick"/>.
    /// </summary>
    public static class CoopSession
    {
        private const float PingInterval = 1f;
        private const float TimeoutSeconds = 20f;
        private const float HelloResendSeconds = 2f;

        private static readonly Dictionary<MsgType, Action<NetReader>> Handlers = new Dictionary<MsgType, Action<NetReader>>();
        private static readonly List<byte[]> Inbox = new List<byte[]>();
        private static readonly NetWriter Writer = new NetWriter(4096);

        private static ITransport _transport;
        private static float _lastReceive;
        private static float _nextPing;
        private static float _nextHello;

        public static CoopMode Mode { get; private set; }
        public static LinkState State { get; private set; }
        public static string PartnerName { get; private set; }
        public static float RttMs { get; private set; }
        public static string LastError { get; set; }
        public static string TransportDescription => _transport?.Describe ?? "-";
        public static long BytesSent { get; private set; }
        public static long BytesReceived { get; private set; }

        public static bool IsHost => Mode == CoopMode.Host;
        public static bool IsClient => Mode == CoopMode.Client;
        public static bool Connected => State == LinkState.Connected;
        public static string RoleTag => Mode == CoopMode.Host ? "HOST" : Mode == CoopMode.Client ? "CLIENT" : "OFF";

        /// <summary>Raised on both sides once the handshake has completed.</summary>
        public static event Action PartnerJoined;
        /// <summary>Raised when the partner leaves or the link fails (argument: reason).</summary>
        public static event Action<string> PartnerLeft;

        public static void Register(MsgType type, Action<NetReader> handler) => Handlers[type] = handler;

        public static string GameFingerprint =>
            typeof(AirDefender.GameSaveManager).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);

        public static string LocalName
        {
            get
            {
                try
                {
                    var p = AirDefender.PlayerProfileManager.SelectedProfile;
                    if (p != null && !string.IsNullOrEmpty(p.callsign)) return p.callsign;
                }
                catch { }
                try { if (SteamManager.Initialized) return SteamFriends.GetPersonaName(); } catch { }
                return "Player";
            }
        }

        // ---------------------------------------------------------------- start / stop

        public static void StartHost(ITransport transport)
        {
            Stop("restart");
            Mode = CoopMode.Host;
            State = LinkState.Waiting;
            _transport = transport;
            LastError = null;
            CoopLog.Info($"Hosting on {transport.Describe}");
        }

        public static void StartClient(ITransport transport)
        {
            Stop("restart");
            Mode = CoopMode.Client;
            State = LinkState.Handshaking;
            _transport = transport;
            LastError = null;
            _lastReceive = Time.realtimeSinceStartup;
            _nextHello = 0f;
            CoopLog.Info($"Joining via {transport.Describe}");
        }

        public static void Stop(string reason)
        {
            if (_transport == null && Mode == CoopMode.Offline) return;
            bool wasConnected = Connected;
            if (wasConnected) SendBye(reason);
            try { _transport?.Close(); } catch { }
            _transport = null;
            Mode = CoopMode.Offline;
            State = LinkState.Offline;
            CoopLog.Info($"Co-op stopped: {reason}");
            if (wasConnected) RaisePartnerLeft(reason);
            PartnerName = null;
        }

        private static void SendBye(string reason)
        {
            try { Send(MsgType.Bye, w => w.Str(reason)); } catch { }
        }

        // ---------------------------------------------------------------- sending

        public static void Send(MsgType type, Action<NetWriter> body, bool reliable = true)
        {
            if (_transport == null) return;
            Writer.Reset();
            Writer.U16((ushort)type);
            body?.Invoke(Writer);
            _transport.Send(Writer.Buffer, 0, Writer.Length, reliable);
            BytesSent += Writer.Length;
        }

        public static void SendRaw(byte[] data, int count, bool reliable)
        {
            if (_transport == null) return;
            _transport.Send(data, 0, count, reliable);
            BytesSent += count;
        }

        // ---------------------------------------------------------------- tick

        public static void Tick()
        {
            if (_transport == null) return;
            float now = Time.realtimeSinceStartup;

            if (_transport.FailureReason != null)
            {
                string why = _transport.FailureReason;
                if (Mode == CoopMode.Host)
                {
                    if (State != LinkState.Waiting) HostLosePartner(why);
                }
                else
                {
                    LastError = why;
                    Stop(why);
                    return;
                }
            }

            Inbox.Clear();
            _transport.Poll(Inbox);
            for (int i = 0; i < Inbox.Count; i++)
            {
                _lastReceive = now;
                BytesReceived += Inbox[i].Length;
                Dispatch(Inbox[i]);
                if (_transport == null) return; // a handler ended the session
            }

            if (Mode == CoopMode.Host && State == LinkState.Waiting && _transport.HasPeer)
            {
                State = LinkState.Handshaking;
                _lastReceive = now;
            }

            if (Mode == CoopMode.Client && State == LinkState.Handshaking && _transport.HasPeer && now >= _nextHello)
            {
                _nextHello = now + HelloResendSeconds;
                Send(MsgType.Hello, w =>
                {
                    w.I32(Protocol.Version);
                    w.Str(Plugin.Version);
                    w.Str(GameFingerprint);
                    w.Str(LocalName);
                });
            }

            if (Connected && now >= _nextPing)
            {
                _nextPing = now + PingInterval;
                Send(MsgType.Ping, w => w.F64(now), reliable: false);
            }

            if (State == LinkState.Connected || State == LinkState.Handshaking)
            {
                if (now - _lastReceive > TimeoutSeconds)
                {
                    if (Mode == CoopMode.Host) HostLosePartner("timed out");
                    else { LastError = "host timed out"; Stop("timed out"); }
                }
            }
        }

        private static void HostLosePartner(string reason)
        {
            CoopLog.Warn($"Partner lost: {reason}");
            bool wasConnected = Connected;
            _transport?.DropPeer();
            State = LinkState.Waiting;
            if (wasConnected) RaisePartnerLeft(reason);
            PartnerName = null;
        }

        private static void RaisePartnerLeft(string reason)
        {
            try { PartnerLeft?.Invoke(reason); } catch (Exception e) { CoopLog.Error("PartnerLeft handler: " + e); }
        }

        private static void Dispatch(byte[] msg)
        {
            var r = new NetReader(msg);
            MsgType type;
            try { type = (MsgType)r.U16(); }
            catch { return; }

            switch (type)
            {
                case MsgType.Hello: OnHello(r); return;
                case MsgType.Welcome: OnWelcome(r); return;
                case MsgType.Reject:
                    LastError = "host refused: " + r.Str();
                    CoopLog.Warn(LastError);
                    Stop("rejected");
                    return;
                case MsgType.Ping:
                    double t = r.F64();
                    Send(MsgType.Pong, w => w.F64(t), reliable: false);
                    return;
                case MsgType.Pong:
                    RttMs = (float)((Time.realtimeSinceStartup - r.F64()) * 1000.0);
                    return;
                case MsgType.Bye:
                    string why = r.Str();
                    CoopLog.Info($"Partner said goodbye: {why}");
                    if (Mode == CoopMode.Host) HostLosePartner("partner left: " + why);
                    else { LastError = "host ended the session: " + why; Stop("host left"); }
                    return;
            }

            if (!Connected) return;
            if (!Handlers.TryGetValue(type, out var h))
            {
                CoopLog.Debug($"no handler for {type}");
                return;
            }
            try { h(r); }
            catch (Exception e) { CoopLog.Error($"handler {type} failed: {e}"); }
        }

        private static void OnHello(NetReader r)
        {
            if (Mode != CoopMode.Host) return;
            int proto = r.I32();
            string modVer = r.Str();
            string fingerprint = r.Str();
            string name = r.Str();
            if (State == LinkState.Connected) { Send(MsgType.Welcome, w => w.Str(LocalName)); return; } // resend
            string problem = null;
            if (proto != Protocol.Version || modVer != Plugin.Version)
                problem = $"mod version mismatch (host {Plugin.Version}, you {modVer})";
            else if (fingerprint != GameFingerprint)
                problem = "game version mismatch - both players need the same game update";
            if (problem != null)
            {
                CoopLog.Warn($"Rejecting {name}: {problem}");
                Send(MsgType.Reject, w => w.Str(problem));
                return;
            }
            PartnerName = name;
            State = LinkState.Connected;
            Send(MsgType.Welcome, w => w.Str(LocalName));
            CoopLog.Info($"Partner joined: {name}");
            try { PartnerJoined?.Invoke(); } catch (Exception e) { CoopLog.Error("PartnerJoined handler: " + e); }
        }

        private static void OnWelcome(NetReader r)
        {
            if (Mode != CoopMode.Client || State == LinkState.Connected) return;
            PartnerName = r.Str();
            State = LinkState.Connected;
            CoopLog.Info($"Connected to host {PartnerName}");
            try { PartnerJoined?.Invoke(); } catch (Exception e) { CoopLog.Error("PartnerJoined handler: " + e); }
        }
    }
}
