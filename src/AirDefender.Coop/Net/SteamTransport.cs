using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace AirDefenderCoop.Net
{
    /// <summary>
    /// Peer-to-peer link over ISteamNetworkingMessages, routed through Steam Datagram Relay
    /// (no port forwarding needed). The host accepts a session only from the partner it expects
    /// (the lobby member); the client sends to the lobby owner.
    /// </summary>
    public sealed class SteamTransport : ITransport
    {
        private const int Channel = 0;

        private readonly IntPtr[] _recv = new IntPtr[64];
        private readonly Func<CSteamID, bool> _acceptFilter;
        private Callback<SteamNetworkingMessagesSessionRequest_t> _onRequest;
        private Callback<SteamNetworkingMessagesSessionFailed_t> _onFailed;
        private CSteamID _peer;
        private string _failure;
        private bool _closed;

        public SteamTransport(CSteamID peer, Func<CSteamID, bool> acceptFilter)
        {
            _peer = peer;
            _acceptFilter = acceptFilter;
            _onRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _onFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); } catch { }
            // World snapshots and the state stream need more than Steam's conservative defaults.
            SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, 256 * 1024);
            SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 2 * 1024 * 1024);
            SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 4 * 1024 * 1024);
        }

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

        public string Describe => _peer.IsValid() ? $"Steam P2P {_peer.m_SteamID}" : "Steam P2P (waiting for partner)";
        public bool HasPeer => _peer.IsValid() && _failure == null;
        public string FailureReason => _failure;
        public CSteamID Peer => _peer;

        /// <summary>The host learns its partner's id from the lobby; set it so sends can start.</summary>
        public void SetPeer(CSteamID peer)
        {
            _peer = peer;
            _failure = null;
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t req)
        {
            var remote = req.m_identityRemote;
            CSteamID id = remote.GetSteamID();
            bool ok = (_peer.IsValid() && id == _peer) || (_acceptFilter != null && _acceptFilter(id));
            CoopLog.Info($"Steam session request from {id.m_SteamID}: {(ok ? "accepted" : "ignored")}");
            if (!ok) return;
            SteamNetworkingMessages.AcceptSessionWithUser(ref remote);
            if (!_peer.IsValid()) _peer = id;
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t f)
        {
            var info = f.m_info;
            CSteamID id = info.m_identityRemote.GetSteamID();
            if (id != _peer) return;
            _failure = $"Steam session failed ({info.m_eEndReason}): {info.m_szEndDebug}";
        }

        public void Send(byte[] data, int offset, int count, bool reliable)
        {
            if (_closed || !_peer.IsValid()) return;
            var ident = new SteamNetworkingIdentity();
            ident.SetSteamID(_peer);
            int flags = (reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_Unreliable)
                        | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr p = h.AddrOfPinnedObject() + offset;
                EResult r = SteamNetworkingMessages.SendMessageToUser(ref ident, p, (uint)count, flags, Channel);
                if (r != EResult.k_EResultOK && reliable)
                    CoopLog.Warn($"Steam send failed: {r} ({count} bytes)");
            }
            finally { h.Free(); }
        }

        public void Poll(List<byte[]> into)
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
                        CSteamID from = msg.m_identityPeer.GetSteamID();
                        if (_peer.IsValid() && from != _peer) continue;
                        var buf = new byte[msg.m_cbSize];
                        Marshal.Copy(msg.m_pData, buf, 0, msg.m_cbSize);
                        into.Add(buf);
                    }
                    finally { SteamNetworkingMessage_t.Release(_recv[i]); }
                }
                if (n < _recv.Length) break;
            }
        }

        public void DropPeer()
        {
            if (_peer.IsValid())
            {
                var ident = new SteamNetworkingIdentity();
                ident.SetSteamID(_peer);
                try { SteamNetworkingMessages.CloseSessionWithUser(ref ident); } catch { }
            }
            _peer = CSteamID.Nil;
            _failure = null;
        }

        public void Close()
        {
            DropPeer();
            _closed = true;
            _onRequest?.Dispose();
            _onFailed?.Dispose();
            _onRequest = null;
            _onFailed = null;
        }
    }
}
