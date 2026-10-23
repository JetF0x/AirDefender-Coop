using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace AirDefenderCoop.Net
{
    /// <summary>
    /// Peer-to-peer links over ISteamNetworkingMessages, routed through Steam Datagram Relay
    /// (no port forwarding needed). The host accepts sessions from lobby members (one session per
    /// client); a client talks only to the lobby owner.
    /// </summary>
    public sealed class SteamTransport : ITransport
    {
        private const int Channel = 0;

        private readonly IntPtr[] _recv = new IntPtr[64];
        private readonly Func<CSteamID, bool> _acceptFilter;
        private readonly bool _isHost;
        private readonly HashSet<ulong> _accepted = new HashSet<ulong>();
        private readonly List<KeyValuePair<ulong, string>> _failed = new List<KeyValuePair<ulong, string>>();
        private Callback<SteamNetworkingMessagesSessionRequest_t> _onRequest;
        private Callback<SteamNetworkingMessagesSessionFailed_t> _onFailed;
        private CSteamID _server;
        private string _failure;
        private bool _closed;

        private SteamTransport(bool isHost, CSteamID server, Func<CSteamID, bool> acceptFilter)
        {
            _isHost = isHost;
            _server = server;
            _acceptFilter = acceptFilter;
            _onRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _onFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); } catch { }
            // World snapshots and the state stream need more than Steam's conservative defaults.
            SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, 256 * 1024);
            SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 2 * 1024 * 1024);
            SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 4 * 1024 * 1024);
        }

        /// <summary>Host: accepts any user the filter allows (the lobby members).</summary>
        public static SteamTransport ForHost(Func<CSteamID, bool> acceptFilter) => new SteamTransport(true, CSteamID.Nil, acceptFilter);

        /// <summary>Client: talks to the host only.</summary>
        public static SteamTransport ForClient(CSteamID host) => new SteamTransport(false, host, null);

        private static void SetGlobalInt(ESteamNetworkingConfigValue key, int value)
        {
            var h = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                SteamNetworkingUtils.SetConfigValue(key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, h.AddrOfPinnedObject());
            }
            catch (Exception e) { CoopLog.Warn($"Steam config {key}: {e.Message}"); }
            finally { h.Free(); }
        }

        public string Describe => _isHost ? $"Steam P2P host ({_accepted.Count} linked)" : $"Steam P2P {_server.m_SteamID}";
        public bool HasPeer => !_isHost && _server.IsValid() && _failure == null;
        public ulong ServerPeer => _server.m_SteamID;
        public string FailureReason => _failure;

        /// <summary>
        /// Host: a user just entered the lobby. Accepts a session request that may already be
        /// pending from them (it can arrive before the lobby member list shows them).
        /// </summary>
        public void Allow(CSteamID id)
        {
            if (!_isHost || _closed) return;
            _accepted.Add(id.m_SteamID);
            var ident = new SteamNetworkingIdentity();
            ident.SetSteamID(id);
            try { SteamNetworkingMessages.AcceptSessionWithUser(ref ident); } catch { }
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t req)
        {
            var remote = req.m_identityRemote;
            CSteamID id = remote.GetSteamID();
            bool ok = _isHost
                ? _accepted.Contains(id.m_SteamID) || (_acceptFilter != null && _acceptFilter(id))
                : id == _server;
            CoopLog.Info($"Steam session request from {id.m_SteamID}: {(ok ? "accepted" : "ignored")}");
            if (!ok) return;
            SteamNetworkingMessages.AcceptSessionWithUser(ref remote);
            if (_isHost) _accepted.Add(id.m_SteamID);
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t f)
        {
            var info = f.m_info;
            CSteamID id = info.m_identityRemote.GetSteamID();
            string why = $"Steam session failed ({info.m_eEndReason}): {info.m_szEndDebug}";
            if (_isHost)
            {
                if (_accepted.Remove(id.m_SteamID)) _failed.Add(new KeyValuePair<ulong, string>(id.m_SteamID, why));
            }
            else if (id == _server) _failure = why;
        }

        public void Send(ulong peer, byte[] data, int offset, int count, bool reliable)
        {
            if (_closed || peer == 0) return;
            var ident = new SteamNetworkingIdentity();
            ident.SetSteamID(new CSteamID(peer));
            int flags = (reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_Unreliable)
                        | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr p = h.AddrOfPinnedObject() + offset;
                EResult r = SteamNetworkingMessages.SendMessageToUser(ref ident, p, (uint)count, flags, Channel);
                if (r != EResult.k_EResultOK && reliable)
                    CoopLog.Warn($"Steam send to {peer} failed: {r} ({count} bytes)");
            }
            finally { h.Free(); }
        }

        public void Poll(List<NetMessage> into)
        {
            if (_closed) return;
            while (true)
            {
                int n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _recv, _recv.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(_recv[i]);
                        ulong from = msg.m_identityPeer.GetSteamID().m_SteamID;
                        if (_isHost ? !_accepted.Contains(from) : from != _server.m_SteamID) continue;
                        var buf = new byte[msg.m_cbSize];
                        Marshal.Copy(msg.m_pData, buf, 0, msg.m_cbSize);
                        into.Add(new NetMessage { From = from, Data = buf });
                    }
                    finally { SteamNetworkingMessage_t.Release(_recv[i]); }
                }
                if (n < _recv.Length) break;
            }
        }

        public void TakeFailedPeers(List<KeyValuePair<ulong, string>> into)
        {
            into.AddRange(_failed);
            _failed.Clear();
        }

        public void DropPeer(ulong peer)
        {
            if (peer == 0) return;
            var ident = new SteamNetworkingIdentity();
            ident.SetSteamID(new CSteamID(peer));
            try { SteamNetworkingMessages.CloseSessionWithUser(ref ident); } catch { }
            _accepted.Remove(peer);
            if (!_isHost && peer == _server.m_SteamID) _server = CSteamID.Nil;
        }

        public void Close()
        {
            if (_isHost) foreach (var p in new List<ulong>(_accepted)) DropPeer(p);
            else DropPeer(_server.m_SteamID);
            _closed = true;
            _onRequest?.Dispose();
            _onFailed?.Dispose();
            _onRequest = null;
            _onFailed = null;
        }
    }
}
