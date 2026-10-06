using System.Collections.Generic;
using AirDefenderCoop.Bootstrap;
using AirDefenderCoop.Net;
using UnityEngine;

namespace AirDefenderCoop.Ui
{
    /// <summary>Short on-screen notices ("Only the host can save during co-op").</summary>
    public static class CoopToast
    {
        private static string _text;
        private static float _until;

        public static void Show(string text, float seconds = 4f)
        {
            _text = text;
            _until = Time.realtimeSinceStartup + seconds;
            CoopLog.Info("Toast: " + text);
        }

        public static void Draw(GUIStyle style)
        {
            if (_text == null || Time.realtimeSinceStartup > _until) return;
            var size = style.CalcSize(new GUIContent(_text));
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            var r = new Rect((Screen.width / scale - size.x) / 2f - 12, Screen.height / scale * 0.12f, size.x + 24, size.y + 12);
            GUI.Box(r, GUIContent.none);
            GUI.Label(new Rect(r.x + 12, r.y + 6, size.x, size.y), _text, style);
        }
    }

    /// <summary>The F9 co-op window (IMGUI, matching the game's own UI technology).</summary>
    public static class CoopPanel
    {
        private static bool _open;
        private static Rect _window = new Rect(40, 80, 430, 10);
        private static string _joinAddress;
        private static GUIStyle _label, _small, _status, _toast;

        public static void Toggle() => _open = !_open;
        public static void Open() => _open = true;

        private static void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            _label.normal.textColor = new Color(0.65f, 1f, 0.65f);
            _small = new GUIStyle(_label) { fontSize = 13 };
            _small.normal.textColor = new Color(0.75f, 0.85f, 0.75f);
            _status = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            _status.normal.textColor = new Color(0.4f, 1f, 0.4f);
            _toast = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            _toast.normal.textColor = new Color(1f, 0.85f, 0.3f);
        }

        public static void OnGUI()
        {
            EnsureStyles();
            GUI.depth = -2000;
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            try { DrawAll(scale); }
            finally { GUI.matrix = oldMatrix; }
        }

        private static void DrawAll(float scale)
        {
            DrawStatusLine();
            CoopToast.Draw(_toast);
            if (!_open) return;
            if (_joinAddress == null) _joinAddress = CoopConfig.LastJoinAddress.Value;
            _window.height = 10;
            _window = GUILayout.Window(0x0AD0C0, _window, DrawWindow, "Air Defender Co-op " + Plugin.Version);
        }

        private static void DrawStatusLine()
        {
            if (CoopSession.Mode == CoopMode.Offline) return;
            string text;
            if (CoopSession.Connected)
                text = $"CO-OP {CoopSession.RoleTag} | {CoopSession.Players.Count}/{(CoopSession.IsHost ? CoopSession.MaxPlayers.ToString() : "?")} players | {CoopSession.RttMs:0} ms | {WorldSync.Status}";
            else if (CoopSession.IsHost)
                text = $"CO-OP HOST | waiting for players ({CoopSession.TransportDescription})  [{CoopConfig.PanelKey.Value}]";
            else
                text = $"CO-OP | connecting... ({CoopSession.TransportDescription})";
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.Label(new Rect(10, Screen.height / scale - 46, Screen.width / scale - 20, 22), text, _status);
        }

        private static void DrawWindow(int id)
        {
            GUILayout.Label(StatusText(), _label);
            DrawPlayerList();
            if (!string.IsNullOrEmpty(CoopSession.LastError)) GUILayout.Label("Last error: " + CoopSession.LastError, _small);
            if (!string.IsNullOrEmpty(SteamLobbyService.Status) && CoopSession.Mode != CoopMode.Offline) GUILayout.Label("Steam: " + SteamLobbyService.Status, _small);
            GUILayout.Space(6);

            switch (CoopSession.Mode)
            {
                case CoopMode.Offline:
                    GUI.enabled = SteamLobbyService.Available;
                    if (GUILayout.Button($"Host co-op (invite up to {CoopSession.MaxPlayers - 1} Steam friends)", GUILayout.Height(30))) SteamLobbyService.Host();
                    GUI.enabled = true;
                    GUILayout.Space(4);
                    GUILayout.Label("Direct connection (LAN / local testing):", _small);
                    if (GUILayout.Button($"Host direct on port {CoopConfig.Port.Value}")) HostDirect();
                    GUILayout.BeginHorizontal();
                    _joinAddress = GUILayout.TextField(_joinAddress ?? "", GUILayout.Width(250));
                    if (GUILayout.Button("Join address")) JoinDirect(_joinAddress);
                    GUILayout.EndHorizontal();
                    break;

                case CoopMode.Host:
                    if (SteamLobbyService.Lobby.IsValid() && CoopSession.HasFreeSlot)
                    {
                        if (GUILayout.Button("Invite with the Steam overlay...", GUILayout.Height(28)))
                            SteamLobbyService.OpenInviteDialog();
                        DrawFriendList();
                    }
                    if (CoopSession.Connected && GUILayout.Button("Resend world to everyone")) WorldSync.RequestResend("host pressed resync");
                    if (GUILayout.Button("Stop hosting")) StopAll("host stopped");
                    break;

                case CoopMode.Client:
                    if (CoopSession.Connected && GUILayout.Button("Request world resync")) CoopSession.Send(MsgType.WorldRequest, null);
                    if (GUILayout.Button("Leave session")) StopAll("player left");
                    break;
            }

            GUILayout.Space(4);
            GUILayout.Label($"Sent {CoopSession.BytesSent / 1024} KB / received {CoopSession.BytesReceived / 1024} KB", _small);
            if (GUILayout.Button("Close")) _open = false;
            GUI.DragWindow();
        }

        private static Vector2 _friendScroll;
        private static List<SteamLobbyService.Friend> _friends;
        private static float _friendsRefreshed = -100f;

        private static void DrawFriendList()
        {
            if (Time.realtimeSinceStartup - _friendsRefreshed > 5f)
            {
                _friends = SteamLobbyService.GetFriends();
                _friendsRefreshed = Time.realtimeSinceStartup;
            }
            if (_friends == null || _friends.Count == 0) return;
            GUILayout.Label("Or invite directly:", _small);
            _friendScroll = GUILayout.BeginScrollView(_friendScroll, GUILayout.Height(160));
            foreach (var f in _friends)
            {
                if (!f.Online || SteamLobbyService.IsInLobby(f.Id)) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label((f.InThisGame ? "[in Air Defender] " : "") + f.Name, _small, GUILayout.Width(300));
                if (GUILayout.Button("Invite", GUILayout.Width(70))) SteamLobbyService.InviteFriend(f.Id);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private static string StatusText()
        {
            switch (CoopSession.Mode)
            {
                case CoopMode.Offline: return "Not in a co-op session.";
                case CoopMode.Host:
                    return CoopSession.Connected
                        ? $"Hosting {CoopSession.Players.Count}/{CoopSession.MaxPlayers} players.\n{WorldSync.Status}"
                        : $"Hosting - waiting for players to join (up to {CoopSession.MaxPlayers}).";
                default:
                    return CoopSession.Connected
                        ? $"Connected to {CoopSession.HostName} ({CoopSession.RttMs:0} ms)\n{WorldSync.Status}"
                        : "Connecting to the host...";
            }
        }

        private static GUIStyle _player;

        /// <summary>Everyone in the session in their overlay colour, with ping; the host can remove players.</summary>
        private static void DrawPlayerList()
        {
            if (!CoopSession.Connected || CoopSession.Players.Count == 0) return;
            if (_player == null) _player = new GUIStyle(_small) { fontStyle = FontStyle.Bold };
            GUILayout.Space(4);
            foreach (var p in CoopSession.Players)
            {
                GUILayout.BeginHorizontal();
                bool me = p.Slot == CoopSession.LocalSlot;
                _player.normal.textColor = me ? new Color(0.4f, 1f, 0.4f) : PartnerOverlay.ColorOf(p.Slot);
                string role = p.Slot == 0 ? " (host)" : "";
                string ping = me ? "" : $"  {p.RttMs:0} ms";
                GUILayout.Label($"{p.Name}{role}{(me ? " - you" : "")}{ping}", _player, GUILayout.Width(330));
                if (CoopSession.IsHost && p.Slot != 0 && GUILayout.Button("Remove", GUILayout.Width(70))) CoopSession.Kick(p.Slot);
                GUILayout.EndHorizontal();
            }
        }

        public static void HostDirect()
        {
            SteamLobbyService.Leave();
            try { CoopSession.StartHost(TcpTransport.Listen(CoopConfig.Port.Value)); }
            catch (System.Exception e) { CoopSession.LastError = "could not listen: " + e.Message; CoopLog.Error(CoopSession.LastError); }
        }

        public static void JoinDirect(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            address = address.Trim();
            CoopConfig.LastJoinAddress.Value = address;
            string host = address;
            int port = CoopConfig.Port.Value;
            int colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.Substring(colon + 1), out int p)) { host = address.Substring(0, colon); port = p; }
            SteamLobbyService.Leave();
            CoopSession.StartClient(TcpTransport.Connect(host, port));
        }

        public static void StopAll(string reason)
        {
            CoopSession.Stop(reason);
            SteamLobbyService.Leave();
        }
    }
}
