using System;
using System.Collections.Generic;
using AirDefenderCoop.Net;
using Steamworks;
using UnityEngine;

namespace AirDefenderCoop
{
    public enum CoopMode { Offline, Host, Client }

    public enum LinkState { Offline, Waiting, Handshaking, Connected }

    /// <summary>One player in the session, as everyone sees it. The host is always slot 0.</summary>
    public sealed class PlayerInfo
    {
        public byte Slot;
        public string Name;
        public float RttMs;
    }

    /// <summary>
    /// Owns the transport, the handshake, heartbeats and message dispatch. The host keeps one entry
    /// per joined client; a client only ever talks to the host. Everything runs on the Unity main
    /// thread via <see cref="Tick"/>.
    /// </summary>
    public static class CoopSession
    {
        private const float PingInterval = 1f;
        private const float PlayerListInterval = 2f;
        private const float TimeoutSeconds = 20f;
        private const float HelloResendSeconds = 2f;
        private const float KickDelaySeconds = 1f;
        public const string RestartReason = "restart";

        private sealed class Peer
        {
            public ulong Id;
            public byte Slot;
            public string Name;
            public float LastReceive;
            public float RttMs;
            public float DropAt = -1f;   // kicked: drop once the Reject has had time to arrive
        }

        private static readonly Dictionary<MsgType, Action<NetReader>> Handlers = new Dictionary<MsgType, Action<NetReader>>();
        private static readonly Dictionary<MsgType, bool> Relayed = new Dictionary<MsgType, bool>(); // type -> reliable
        private static readonly List<NetMessage> Inbox = new List<NetMessage>();
        private static readonly List<KeyValuePair<ulong, string>> Failed = new List<KeyValuePair<ulong, string>>();
        private static readonly NetWriter Writer = new NetWriter(4096);
        private static readonly Dictionary<ulong, Peer> Peers = new Dictionary<ulong, Peer>();
        private static readonly List<ulong> PeerIdList = new List<ulong>();
        private static List<PlayerInfo> _players = new List<PlayerInfo>();

        private static ITransport _transport;
        private static float _lastReceive;
        private static float _nextPing;
        private static float _nextPlayerList;
        private static float _nextHello;
        private static int _tickDepth;

        public static CoopMode Mode { get; private set; }
        public static LinkState State { get; private set; }
        /// <summary>Client: the host's callsign.</summary>
        public static string HostName { get; private set; }
        /// <summary>Client: round trip to the host. Host: the slowest client.</summary>
        public static float RttMs { get; private set; }
        public static string LastError { get; set; }
        public static string TransportDescription => _transport?.Describe ?? "-";
        public static long BytesSent { get; private set; }
        public static long BytesReceived { get; private set; }
        /// <summary>This process's player slot (host 0; a client learns its slot from the host).</summary>
        public static byte LocalSlot { get; private set; }

        public static bool IsHost => Mode == CoopMode.Host;
        public static bool IsClient => Mode == CoopMode.Client;
        /// <summary>Client: linked to the host. Host: at least one client has joined.</summary>
        public static bool Connected => State == LinkState.Connected;
        public static string RoleTag => Mode == CoopMode.Host ? "HOST" : Mode == CoopMode.Client ? "CLIENT" : "OFF";

        /// <summary>Everyone in the session including this player, ordered by slot.</summary>
        public static IReadOnlyList<PlayerInfo> Players => _players;
        /// <summary>Host: the peer ids of every joined client.</summary>
        public static IReadOnlyList<ulong> PeerIds => PeerIdList;
        public static int MaxPlayers => Mathf.Clamp(CoopConfig.CliMaxPlayers > 0 ? CoopConfig.CliMaxPlayers : CoopConfig.MaxPlayers.Value, 2, 16);
        /// <summary>Host: true while another client can still join.</summary>
        public static bool HasFreeSlot => Peers.Count + 1 < MaxPlayers;

        /// <summary>Host: the peer whose message is being handled right now (0 outside dispatch).</summary>
        public static ulong CurrentSender { get; private set; }
        /// <summary>The callsign of whoever sent the message being handled.</summary>
        public static string SenderName =>
            IsHost ? (Peers.TryGetValue(CurrentSender, out var p) ? p.Name : "Partner") : HostName ?? "Host";

        /// <summary>
        /// Client: linked to the host (handshake done). Host: the first client joined. Replication
        /// that only cares whether anyone is listening subscribes here.
        /// </summary>
        public static event Action PartnerJoined;
        /// <summary>Client: the link to the host ended. Host: the last client left (argument: reason).</summary>
        public static event Action<string> PartnerLeft;
        /// <summary>Host: one client completed the handshake.</summary>
        public static event Action<ulong> PeerJoined;
        /// <summary>Host: one client left or was dropped (peer id, reason).</summary>
        public static event Action<ulong, string> PeerLeft;
        /// <summary>The session ended for any reason (including <see cref="RestartReason"/>).</summary>
        public static event Action<string> Stopped;

        public static void Register(MsgType type, Action<NetReader> handler) => Handlers[type] = handler;

        /// <summary>Messages a client sends that the host also forwards to every other client.</summary>
        /// <remarks>The body must start with the sender's slot (U8); the host overwrites it with the real slot.</remarks>
        public static void RegisterRelayed(MsgType type, Action<NetReader> handler, bool reliable)
        {
            Handlers[type] = handler;
            Relayed[type] = reliable;
        }

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

        public static string NameOfSlot(byte slot)
        {
            foreach (var p in _players) if (p.Slot == slot) return p.Name;
            return "Player " + (slot + 1);
        }

        // ---------------------------------------------------------------- start / stop

        public static void StartHost(ITransport transport)
        {
            Stop(RestartReason);
            Mode = CoopMode.Host;
            State = LinkState.Waiting;
            _transport = transport;
            LastError = null;
            LocalSlot = 0;
            RebuildHostPlayers();
            CoopLog.Info($"Hosting on {transport.Describe} (up to {MaxPlayers} players)");
        }

        public static void StartClient(ITransport transport)
        {
            Stop(RestartReason);
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
            bool wasHost = IsHost;
            if (wasConnected) SendBye(reason);
            try { _transport?.Close(); } catch { }
            _transport = null;
            Mode = CoopMode.Offline;
            State = LinkState.Offline;
            CoopLog.Info($"Co-op stopped: {reason}");
            if (wasHost)
            {
                foreach (var id in new List<ulong>(Peers.Keys)) RaisePeerLeft(id, reason);
                Peers.Clear();
                PeerIdList.Clear();
            }
            if (wasConnected) RaisePartnerLeft(reason);
            HostName = null;
            RttMs = 0f;
            LocalSlot = 0;
            _players = new List<PlayerInfo>();
            try { Stopped?.Invoke(reason); } catch (Exception e) { CoopLog.Error("Stopped handler: " + e); }
        }

        private static void SendBye(string reason)
        {
            try { Send(MsgType.Bye, w => w.Str(reason)); } catch { }
        }

        // ---------------------------------------------------------------- sending

        /// <summary>Host: to every joined client. Client: to the host.</summary>
        public static void Send(MsgType type, Action<NetWriter> body, bool reliable = true)
        {
            if (_transport == null) return;
            Write(type, body);
            SendRaw(Writer.Buffer, Writer.Length, reliable);
        }

        /// <summary>Host: to one client.</summary>
        public static void SendTo(ulong peer, MsgType type, Action<NetWriter> body, bool reliable = true)
        {
            if (_transport == null) return;
            Write(type, body);
            SendRawTo(peer, Writer.Buffer, Writer.Length, reliable);
        }

        /// <summary>Answer whoever sent the message being handled (the host, on a client).</summary>
        public static void Reply(MsgType type, Action<NetWriter> body, bool reliable = true)
        {
            if (IsHost) SendTo(CurrentSender, type, body, reliable);
            else Send(type, body, reliable);
        }

        private static void Write(MsgType type, Action<NetWriter> body)
        {
            Writer.Reset();
            Writer.U16((ushort)type);
            body?.Invoke(Writer);
        }

        /// <summary>Host: to every joined client. Client: to the host.</summary>
        public static void SendRaw(byte[] data, int count, bool reliable)
        {
            if (_transport == null) return;
            if (IsHost)
            {
                for (int i = 0; i < PeerIdList.Count; i++) SendRawTo(PeerIdList[i], data, count, reliable);
            }
            else SendRawTo(_transport.ServerPeer, data, count, reliable);
        }

        public static void SendRawTo(ulong peer, byte[] data, int count, bool reliable)
        {
            if (_transport == null || peer == 0) return;
            _transport.Send(peer, data, 0, count, reliable);
            BytesSent += count;
        }

        // ---------------------------------------------------------------- tick

        public static void Tick()
        {
            if (_transport == null) return;
            float now = Time.realtimeSinceStartup;

            if (Mode == CoopMode.Host)
            {
                Failed.Clear();
                _transport.TakeFailedPeers(Failed);
                foreach (var f in Failed) HostDropPeer(f.Key, f.Value);
            }
            else if (_transport.FailureReason != null)
            {
                string why = _transport.FailureReason;
                LastError = why;
                Stop(why);
                return;
            }

            // A blocking routed call can tick the session from inside a handler; give it its own list.
            var inbox = _tickDepth == 0 ? Inbox : new List<NetMessage>();
            inbox.Clear();
            _tickDepth++;
            try
            {
                _transport.Poll(inbox);
                for (int i = 0; i < inbox.Count; i++)
                {
                    BytesReceived += inbox[i].Data.Length;
                    Dispatch(inbox[i].From, inbox[i].Data, now);
                    if (_transport == null) return; // a handler ended the session
                }
            }
            finally { _tickDepth--; }

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

            if (Mode == CoopMode.Host)
            {
                List<ulong> drop = null;
                foreach (var p in Peers.Values)
                {
                    if (p.DropAt >= 0f ? now >= p.DropAt : now - p.LastReceive > TimeoutSeconds)
                        (drop ?? (drop = new List<ulong>())).Add(p.Id);
                }
                if (drop != null)
                    foreach (var id in drop)
                        HostDropPeer(id, Peers.TryGetValue(id, out var p) && p.DropAt >= 0f ? "removed by the host" : "timed out");

                if (Connected && now >= _nextPlayerList)
                {
                    _nextPlayerList = now + PlayerListInterval;
                    RebuildHostPlayers();
                    BroadcastPlayerList(reliable: false);
                }
            }
            else if ((State == LinkState.Connected || State == LinkState.Handshaking) && now - _lastReceive > TimeoutSeconds)
            {
                LastError = "host timed out";
                Stop("timed out");
            }
        }

        // ---------------------------------------------------------------- host: players

        /// <summary>Host: removes one client (it left the lobby, its link failed, it timed out...).</summary>
        public static void HostDropPeer(ulong id, string reason)
        {
            if (Mode != CoopMode.Host || _transport == null) return;
            _transport.DropPeer(id);
            if (!Peers.TryGetValue(id, out var p)) return;
            Peers.Remove(id);
            PeerIdList.Remove(id);
            CoopLog.Warn($"Player {p.Name} (slot {p.Slot}) left: {reason}");
            Ui.CoopToast.Show($"{p.Name} left the session");
            RaisePeerLeft(id, reason);
            RebuildHostPlayers();
            if (Peers.Count == 0)
            {
                State = LinkState.Waiting;
                RaisePartnerLeft(reason);
            }
            else BroadcastPlayerList(reliable: true);
        }

        /// <summary>Host: tells a client it has been removed, then drops it.</summary>
        public static void Kick(byte slot)
        {
            if (!IsHost) return;
            foreach (var p in Peers.Values)
            {
                if (p.Slot != slot || p.DropAt >= 0f) continue;
                SendTo(p.Id, MsgType.Reject, w => w.Str("removed by the host"));
                p.DropAt = Time.realtimeSinceStartup + KickDelaySeconds;
                CoopLog.Info($"Kicking {p.Name} (slot {slot})");
                return;
            }
        }

        private static byte FreeSlot()
        {
            for (int s = 1; s < 255; s++)
            {
                bool used = false;
                foreach (var p in Peers.Values) if (p.Slot == s) { used = true; break; }
                if (!used) return (byte)s;
            }
            return 255;
        }

        private static void RebuildHostPlayers()
        {
            var list = new List<PlayerInfo> { new PlayerInfo { Slot = 0, Name = LocalName, RttMs = 0f } };
            float worst = 0f;
            foreach (var p in Peers.Values)
            {
                list.Add(new PlayerInfo { Slot = p.Slot, Name = p.Name, RttMs = p.RttMs });
                worst = Mathf.Max(worst, p.RttMs);
            }
            list.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            _players = list;
            RttMs = worst;
        }

        private static void BroadcastPlayerList(bool reliable)
        {
            Send(MsgType.PlayerList, w =>
            {
                w.U8((byte)_players.Count);
                foreach (var p in _players) { w.U8(p.Slot); w.Str(p.Name); w.F32(p.RttMs); }
            }, reliable);
        }

        // ---------------------------------------------------------------- dispatch

        private static void RaisePartnerLeft(string reason)
        {
            try { PartnerLeft?.Invoke(reason); } catch (Exception e) { CoopLog.Error("PartnerLeft handler: " + e); }
        }

        private static void RaisePeerLeft(ulong id, string reason)
        {
            try { PeerLeft?.Invoke(id, reason); } catch (Exception e) { CoopLog.Error("PeerLeft handler: " + e); }
        }

        private static void Dispatch(ulong from, byte[] msg, float now)
        {
            var r = new NetReader(msg);
            MsgType type;
            try { type = (MsgType)r.U16(); }
            catch { return; }

            Peer peer = null;
            if (IsHost)
            {
                if (type == MsgType.Hello) { OnHello(from, r, now); return; }
                // Anything else needs a completed handshake.
                if (!Peers.TryGetValue(from, out peer) || peer.DropAt >= 0f) return;
                peer.LastReceive = now;
            }
            else
            {
                if (from != _transport.ServerPeer) return;
                _lastReceive = now;
            }

            switch (type)
            {
                case MsgType.Welcome: OnWelcome(r); return;
                case MsgType.Reject:
                    if (IsHost) return;
                    LastError = "host refused: " + r.Str();
                    CoopLog.Warn(LastError);
                    Stop("rejected");
                    return;
                case MsgType.Ping:
                    double t = r.F64();
                    SendRawPong(from, t);
                    return;
                case MsgType.Pong:
                    float rtt = (float)((Time.realtimeSinceStartup - r.F64()) * 1000.0);
                    if (peer != null) peer.RttMs = rtt; else RttMs = rtt;
                    return;
                case MsgType.Bye:
                    string why = r.Str();
                    if (IsHost) HostDropPeer(from, "left: " + why);
                    else
                    {
                        CoopLog.Info($"Host said goodbye: {why}");
                        LastError = "host ended the session: " + why;
                        Stop("host left");
                    }
                    return;
                case MsgType.PlayerList:
                    if (!IsHost) OnPlayerList(r);
                    return;
            }

            if (!Connected) return;
            if (IsHost && Relayed.TryGetValue(type, out bool relayReliable) && msg.Length > 2)
            {
                msg[2] = peer.Slot;
                foreach (var id in PeerIdList) if (id != from) SendRawTo(id, msg, msg.Length, relayReliable);
            }
            if (!Handlers.TryGetValue(type, out var h))
            {
                CoopLog.Debug($"no handler for {type}");
                return;
            }
            CurrentSender = from;
            try { h(r); }
            catch (Exception e) { CoopLog.Error($"handler {type} failed: {e}"); }
            finally { CurrentSender = 0; }
        }

        private static void SendRawPong(ulong to, double t)
        {
            Write(MsgType.Pong, w => w.F64(t));
            SendRawTo(IsHost ? to : _transport.ServerPeer, Writer.Buffer, Writer.Length, reliable: false);
        }

        private static void OnHello(ulong from, NetReader r, float now)
        {
            int proto = r.I32();
            string modVer = r.Str();
            string fingerprint = r.Str();
            string name = r.Str();
            if (Peers.TryGetValue(from, out var existing))
            {
                // Our Welcome was lost; say it again.
                existing.LastReceive = now;
                SendTo(from, MsgType.Welcome, w => { w.Str(LocalName); w.U8(existing.Slot); });
                return;
            }
            string problem = null;
            if (proto != Protocol.Version || modVer != Plugin.Version)
                problem = $"mod version mismatch (host {Plugin.Version}, you {modVer})";
            else if (fingerprint != GameFingerprint)
                problem = "game version mismatch - everyone needs the same game update";
            else if (!HasFreeSlot)
                problem = $"the session is full ({MaxPlayers} players)";
            if (problem != null)
            {
                CoopLog.Warn($"Rejecting {name}: {problem}");
                SendTo(from, MsgType.Reject, w => w.Str(problem));
                return;
            }
            var p = new Peer { Id = from, Slot = FreeSlot(), Name = string.IsNullOrEmpty(name) ? "Player" : name, LastReceive = now };
            Peers[from] = p;
            PeerIdList.Add(from);
            bool first = Peers.Count == 1;
            State = LinkState.Connected;
            SendTo(from, MsgType.Welcome, w => { w.Str(LocalName); w.U8(p.Slot); });
            RebuildHostPlayers();
            BroadcastPlayerList(reliable: true);
            CoopLog.Info($"Player joined: {p.Name} (slot {p.Slot}, {Peers.Count + 1}/{MaxPlayers})");
            Ui.CoopToast.Show($"{p.Name} joined the session");
            try { PeerJoined?.Invoke(from); } catch (Exception e) { CoopLog.Error("PeerJoined handler: " + e); }
            if (first)
            {
                try { PartnerJoined?.Invoke(); } catch (Exception e) { CoopLog.Error("PartnerJoined handler: " + e); }
            }
        }

        private static void OnWelcome(NetReader r)
        {
            if (Mode != CoopMode.Client || State == LinkState.Connected) return;
            HostName = r.Str();
            LocalSlot = r.U8();
            State = LinkState.Connected;
            CoopLog.Info($"Connected to host {HostName} as slot {LocalSlot}");
            try { PartnerJoined?.Invoke(); } catch (Exception e) { CoopLog.Error("PartnerJoined handler: " + e); }
        }

        private static void OnPlayerList(NetReader r)
        {
            int n = r.U8();
            var list = new List<PlayerInfo>(n);
            for (int i = 0; i < n; i++) list.Add(new PlayerInfo { Slot = r.U8(), Name = r.Str(), RttMs = r.F32() });
            // Our own row and the host's row both show our measured round trip to the host.
            foreach (var p in list) if (p.Slot == LocalSlot || p.Slot == 0) p.RttMs = RttMs;
            _players = list;
        }
    }
}
