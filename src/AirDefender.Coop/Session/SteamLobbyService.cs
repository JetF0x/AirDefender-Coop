using System;
using System.Collections.Generic;
using AirDefenderCoop.Net;
using Steamworks;

namespace AirDefenderCoop
{
    /// <summary>
    /// Steam invites: the host makes a friends-only lobby with two slots and opens the overlay
    /// invite dialog. Accepting an invite either fires GameLobbyJoinRequested_t (game running) or
    /// starts the game with "+connect_lobby &lt;id&gt;" (handled through CoopConfig). The joiner
    /// enters the lobby, then opens a P2P link to the lobby owner.
    /// </summary>
    public static class SteamLobbyService
    {
        private const string KeyMod = "adcoop";
        private const string KeyVersion = "adcoop_ver";

        private static Callback<GameLobbyJoinRequested_t> _joinRequested;
        private static Callback<LobbyEnter_t> _lobbyEnter;
        private static Callback<LobbyChatUpdate_t> _chatUpdate;
        private static CallResult<LobbyCreated_t> _created;

        public static CSteamID Lobby { get; private set; } = CSteamID.Nil;
        public static string Status { get; private set; } = "";
        public static bool Available
        {
            get { try { return SteamManager.Initialized; } catch { return false; } }
        }

        private static SteamTransport _hostTransport;
        private static ulong _pendingJoin;

        public static void Init()
        {
            if (_joinRequested != null || !Available) return;
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _lobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
            _created = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            CoopLog.Info("Steam lobby service ready");
        }

        /// <summary>Called every frame; picks up a +connect_lobby launch once Steam is up.</summary>
        public static void Tick()
        {
            if (_joinRequested == null) { if (Available) Init(); else return; }
            if (CoopConfig.CliConnectLobby != 0)
            {
                _pendingJoin = CoopConfig.CliConnectLobby;
                CoopConfig.CliConnectLobby = 0;
                CoopLog.Info($"Launched from a Steam invite (lobby {_pendingJoin})");
            }
            if (_pendingJoin != 0)
            {
                ulong id = _pendingJoin;
                _pendingJoin = 0;
                Join(new CSteamID(id));
            }
        }

        public static bool HasPendingInvite => _pendingJoin != 0;

        // ---------------------------------------------------------------- host

        public static void Host()
        {
            if (!Available) { Status = "Steam is not available"; return; }
            Init();
            Leave();
            _hostTransport = new SteamTransport(CSteamID.Nil, IsLobbyMember);
            CoopSession.StartHost(_hostTransport);
            Status = "Creating Steam lobby...";
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
            _created.Set(call);
        }

        private static void OnLobbyCreated(LobbyCreated_t r, bool ioFailure)
        {
            if (ioFailure || r.m_eResult != EResult.k_EResultOK)
            {
                Status = $"Lobby creation failed ({r.m_eResult})";
                CoopLog.Warn(Status);
                return;
            }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, KeyMod, "1");
            SteamMatchmaking.SetLobbyData(Lobby, KeyVersion, Plugin.Version);
            SteamMatchmaking.SetLobbyJoinable(Lobby, true);
            try
            {
                SteamFriends.SetRichPresence("status", "Hosting Air Defender co-op");
                SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);
            }
            catch { }
            Status = "Lobby ready - invite a friend";
            Ui.CoopPanel.Open();
            CoopLog.Info($"Steam lobby created: {Lobby.m_SteamID}");
        }

        public static void OpenInviteDialog()
        {
            if (!Lobby.IsValid()) return;
            SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        public struct Friend
        {
            public CSteamID Id;
            public string Name;
            public bool InThisGame;
            public bool Online;
        }

        /// <summary>Steam friends, those running Air Defender first (for the in-game invite list).</summary>
        public static List<Friend> GetFriends()
        {
            var list = new List<Friend>();
            if (!Available) return list;
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                var state = SteamFriends.GetFriendPersonaState(id);
                bool inGame = SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t info) && info.m_gameID.AppID() == SteamUtils.GetAppID();
                list.Add(new Friend
                {
                    Id = id,
                    Name = SteamFriends.GetFriendPersonaName(id),
                    InThisGame = inGame,
                    Online = state != EPersonaState.k_EPersonaStateOffline,
                });
            }
            list.Sort((a, b) =>
            {
                int c = b.InThisGame.CompareTo(a.InThisGame);
                if (c != 0) return c;
                c = b.Online.CompareTo(a.Online);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>Sends a Steam invite without needing the overlay.</summary>
        public static bool InviteFriend(CSteamID friend)
        {
            if (!Lobby.IsValid()) return false;
            bool ok = SteamMatchmaking.InviteUserToLobby(Lobby, friend);
            CoopLog.Info($"Invited {friend.m_SteamID} to lobby: {ok}");
            Status = ok ? "Invite sent to " + SteamFriends.GetFriendPersonaName(friend) : "Invite failed";
            return ok;
        }

        private static bool IsLobbyMember(CSteamID id)
        {
            if (!Lobby.IsValid()) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == id) return true;
            return false;
        }

        private static void OnChatUpdate(LobbyChatUpdate_t u)
        {
            if (u.m_ulSteamIDLobby != Lobby.m_SteamID) return;
            var who = new CSteamID(u.m_ulSteamIDUserChanged);
            var change = (EChatMemberStateChange)u.m_rgfChatMemberStateChange;
            CoopLog.Info($"Lobby member {who.m_SteamID} change {change}");
            if (CoopSession.IsHost && _hostTransport != null && (change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                if (who != SteamUser.GetSteamID()) _hostTransport.SetPeer(who);
            }
        }

        // ---------------------------------------------------------------- join

        private static void OnJoinRequested(GameLobbyJoinRequested_t r)
        {
            CoopLog.Info($"Invite accepted from {r.m_steamIDFriend.m_SteamID} (lobby {r.m_steamIDLobby.m_SteamID})");
            Join(r.m_steamIDLobby);
        }

        public static void Join(CSteamID lobby)
        {
            if (!Available) return;
            Init();
            Leave();
            Status = "Joining Steam lobby...";
            SteamMatchmaking.JoinLobby(lobby);
        }

        private static void OnLobbyEnter(LobbyEnter_t e)
        {
            var lobby = new CSteamID(e.m_ulSteamIDLobby);
            if (CoopSession.IsHost && lobby == Lobby) return; // host's own entry
            if (e.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Status = $"Could not join lobby ({(EChatRoomEnterResponse)e.m_EChatRoomEnterResponse})";
                CoopLog.Warn(Status);
                return;
            }
            Lobby = lobby;
            string ver = SteamMatchmaking.GetLobbyData(lobby, KeyVersion);
            if (!string.IsNullOrEmpty(ver) && ver != Plugin.Version)
            {
                Status = $"Host runs co-op mod {ver}, you have {Plugin.Version}";
                CoopSession.LastError = Status;
                CoopLog.Warn(Status);
                Leave();
                return;
            }
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby);
            Status = "Connecting to host...";
            CoopLog.Info($"Entered lobby {lobby.m_SteamID}, host {owner.m_SteamID}");
            CoopSession.StartClient(new SteamTransport(owner, null));
        }

        public static void Leave()
        {
            if (Lobby.IsValid())
            {
                try { SteamMatchmaking.LeaveLobby(Lobby); } catch { }
                CoopLog.Info($"Left lobby {Lobby.m_SteamID}");
            }
            Lobby = CSteamID.Nil;
            _hostTransport = null;
            try { SteamFriends.ClearRichPresence(); } catch { }
        }
    }

    /// <summary>A client can only load the host's world once a player profile is selected.</summary>
    public static class JoinGate
    {
        public static bool CanJoinNow
        {
            get
            {
                try { return AirDefender.PlayerProfileManager.SelectedProfile != null; }
                catch { return false; }
            }
        }
    }
}
