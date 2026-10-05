using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using AirDefender;
using AirDefenderCoop.Net;
using BepInEx;
using UnityEngine;

namespace AirDefenderCoop.Bootstrap
{
    /// <summary>
    /// Gets the client into the host's world. The host serialises its live world with the game's
    /// own save snapshot, streams it in chunks, and the client loads it through the game's normal
    /// load path. Used on join, after the host loads a save, and as a hard resync.
    /// </summary>
    public static class WorldSync
    {
        private const int ChunkSize = 128 * 1024;
        public const string SessionName = "Co-op session";

        // Client receive state.
        private static int _rxId = -1;
        private static byte[][] _rxChunks;
        private static int _rxCount;
        private static int _rxRawLength;
        private static string _rxAor, _rxTheatre;
        private static byte[] _pendingWorld; // compressed world waiting for the player to pick a profile

        private static int _txId;
        private static bool _hostWorldSent;

        /// <summary>Client: the host world has been loaded and live replication may apply.</summary>
        public static bool ClientWorldReady { get; private set; }
        /// <summary>Host: the client confirmed it loaded the current world.</summary>
        public static bool PartnerWorldReady { get; private set; }
        /// <summary>True while this process is applying a world it received.</summary>
        public static bool ApplyingRemoteWorld { get; private set; }
        public static string Status { get; private set; } = "";
        public static float TransferProgress => _rxChunks == null ? 0f : (float)_rxCount / _rxChunks.Length;
        public static int WorldGeneration { get; private set; }

        public static event Action ClientWorldLoaded;
        public static event Action HostWorldSent;
        /// <summary>Host: the client listed the contacts present after its load.</summary>
        public static event Action<System.Collections.Generic.List<string>> PartnerContactsReported;

        /// <summary>Client: tell the host which contacts exist locally after loading its world.</summary>
        public static void ReportClientContacts(System.Collections.Generic.List<string> ids)
        {
            CoopSession.Send(MsgType.ClientContacts, w => { w.I32(WorldGeneration); w.StrList(ids); });
            CoopLog.Info($"Reported {ids.Count} local contacts to the host");
        }

        public static void Init()
        {
            CoopSession.Register(MsgType.WorldBegin, OnWorldBegin);
            CoopSession.Register(MsgType.WorldChunk, OnWorldChunk);
            CoopSession.Register(MsgType.WorldEnd, OnWorldEnd);
            CoopSession.Register(MsgType.WorldLoaded, OnWorldLoaded);
            CoopSession.Register(MsgType.WorldRequest, _ => { if (CoopSession.IsHost) RequestResend("client asked"); });
            CoopSession.Register(MsgType.ReturnToMenu, OnReturnToMenu);
            CoopSession.Register(MsgType.ClientContacts, r =>
            {
                if (!CoopSession.IsHost) return;
                r.I32();
                var ids = r.StrList();
                CoopLog.Info($"Client reports {ids.Count} contacts after load");
                try { PartnerContactsReported?.Invoke(ids); } catch (Exception e) { CoopLog.Error("PartnerContactsReported: " + e); }
            });
            CoopSession.PartnerJoined += () =>
            {
                _hostWorldSent = false;
                PartnerWorldReady = false;
                ClientWorldReady = false;
                Status = CoopSession.IsHost ? "Partner connected" : "Waiting for the host's world";
            };
            CoopSession.PartnerLeft += reason =>
            {
                bool wasInHostWorld = CoopSession.Mode != CoopMode.Host && ClientWorldReady;
                PartnerWorldReady = false;
                ClientWorldReady = false;
                if (wasInHostWorld && PlayerSession.IsLoggedIn())
                {
                    // The world belonged to the host; do not leave the player in a frozen copy of it.
                    Ui.CoopToast.Show("The host ended the session - returning to the main menu", 6f);
                    try { GameResetService.ResetToMainMenu(); } catch (Exception e) { CoopLog.Error("Return to menu failed: " + e); }
                }
                _rxChunks = null;
                _pendingWorld = null;
            };
        }

        public static void Tick()
        {
            if (!CoopSession.Connected) return;
            if (CoopSession.IsHost)
            {
                if (AirDefender.TrainingSessionState.IsTraining)
                {
                    Status = "Training missions are single-player - start a normal game to play together";
                    return;
                }
                if (!_hostWorldSent && PlayerSession.IsLoggedIn() && PlayerSession.SecondsSinceLogin() > 3f && !GameSaveManager.HasLoadedRecently())
                    SendWorld();
            }
            else if (_pendingWorld != null && JoinGate.CanJoinNow)
            {
                var w = _pendingWorld;
                _pendingWorld = null;
                ApplyWorld(w);
            }
        }

        /// <summary>Host: forget the sent world so the next tick sends a fresh one.</summary>
        public static void RequestResend(string reason)
        {
            if (!CoopSession.IsHost) return;
            CoopLog.Info($"World resend requested: {reason}");
            _hostWorldSent = false;
            PartnerWorldReady = false;
        }

        // ---------------------------------------------------------------- host

        private static void SendWorld()
        {
            _hostWorldSent = true;
            PartnerWorldReady = false;
            var sw = Stopwatch.StartNew();
            string json;
            try
            {
                var blob = GameSaveManager.BuildSnapshot();
                blob.name = SessionName;
                blob.savedUtc = DateTime.UtcNow.ToString("o");
                json = JsonUtility.ToJson(blob);
            }
            catch (Exception e)
            {
                CoopLog.Error("Could not snapshot the world: " + e);
                Status = "World snapshot failed";
                return;
            }
            long snapMs = sw.ElapsedMilliseconds;
            byte[] raw = Encoding.UTF8.GetBytes(json);
            byte[] packed = Compress(raw);
            int id = ++_txId;
            int chunks = (packed.Length + ChunkSize - 1) / ChunkSize;
            string aor = PlayerSession.GetAor().ToString();
            string theatre = PlayerSession.GetTheatre().ToString();
            CoopSession.Send(MsgType.WorldBegin, w => { w.I32(id); w.I32(chunks); w.I32(raw.Length); });
            for (int i = 0; i < chunks; i++)
            {
                int off = i * ChunkSize;
                int len = Math.Min(ChunkSize, packed.Length - off);
                int index = i;
                CoopSession.Send(MsgType.WorldChunk, w => { w.I32(id); w.I32(index); w.Bytes(packed, off, len); });
            }
            CoopSession.Send(MsgType.WorldEnd, w => { w.I32(id); w.Str(aor); w.Str(theatre); });
            Status = $"World sent ({packed.Length / 1024} KB)";
            CoopLog.Info($"World #{id} sent: json {raw.Length} B, packed {packed.Length} B in {chunks} chunks; snapshot {snapMs} ms, total {sw.ElapsedMilliseconds} ms");
            try { HostWorldSent?.Invoke(); } catch (Exception e) { CoopLog.Error("HostWorldSent: " + e); }
        }

        private static void OnWorldLoaded(NetReader r)
        {
            int id = r.I32();
            bool ok = r.Bool();
            string err = r.Str();
            if (id != _txId) return;
            PartnerWorldReady = ok;
            Status = ok ? "Partner is in the world" : "Partner failed to load: " + err;
            CoopLog.Info($"Client loaded world #{id}: {ok} {err}");
        }

        /// <summary>Host returned to the main menu: send the partner there too.</summary>
        public static void HostReturnedToMenu()
        {
            if (!CoopSession.IsHost || !CoopSession.Connected) return;
            _hostWorldSent = false;
            PartnerWorldReady = false;
            CoopSession.Send(MsgType.ReturnToMenu, null);
        }

        // ---------------------------------------------------------------- client

        private static void OnWorldBegin(NetReader r)
        {
            _rxId = r.I32();
            _rxChunks = new byte[r.I32()][];
            _rxRawLength = r.I32();
            _rxCount = 0;
            ClientWorldReady = false;
            Status = "Receiving the host's world...";
        }

        private static void OnWorldChunk(NetReader r)
        {
            int id = r.I32();
            int index = r.I32();
            byte[] data = r.Bytes();
            if (id != _rxId || _rxChunks == null || index < 0 || index >= _rxChunks.Length) return;
            if (_rxChunks[index] == null) { _rxChunks[index] = data; _rxCount++; }
        }

        private static void OnWorldEnd(NetReader r)
        {
            int id = r.I32();
            _rxAor = r.Str();
            _rxTheatre = r.Str();
            if (id != _rxId || _rxChunks == null || _rxCount != _rxChunks.Length)
            {
                CoopLog.Warn($"World #{id} incomplete ({_rxCount}/{_rxChunks?.Length}); asking again");
                CoopSession.Send(MsgType.WorldRequest, null);
                return;
            }
            using (var ms = new MemoryStream())
            {
                foreach (var c in _rxChunks) ms.Write(c, 0, c.Length);
                _pendingWorld = ms.ToArray();
            }
            _rxChunks = null;
            if (!JoinGate.CanJoinNow) Status = "Select your callsign to join the host";
        }

        private static void ApplyWorld(byte[] packed)
        {
            int id = _rxId;
            string error = null;
            var sw = Stopwatch.StartNew();
            ApplyingRemoteWorld = true;
            try
            {
                byte[] raw = Decompress(packed);
                if (raw.Length != _rxRawLength) throw new InvalidDataException($"world size {raw.Length} != {_rxRawLength}");
                string dir = Path.Combine(Paths.BepInExRootPath, "coop_tmp");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"coop_world_{System.Diagnostics.Process.GetCurrentProcess().Id}.json");
                File.WriteAllBytes(path, raw);

                bool wasInGame = PlayerSession.IsLoggedIn();
                PlayerSession.Theatre theatreBefore = PlayerSession.GetTheatre();
                if (!GameSaveManager.LoadFromPath(path, SessionName))
                    throw new Exception("the game refused to load the host's world (see BepInEx log)");

                PlayerSession.Theatre theatre = GameSaveManager.GetLastLoadedTheatre();
                if (!wasInGame)
                {
                    try { SfxService.SetMenuOnlyAudioMode(enabled: false); } catch { }
                    AorManager.Aor aor = GameSaveManager.GetLastLoadedAor();
                    if (theatre == PlayerSession.Theatre.UK && aor == AorManager.Aor.None)
                        aor = Enum.TryParse(_rxAor, out AorManager.Aor a) && a != AorManager.Aor.None ? a : AorManager.Aor.North;
                    PlayerSession.Login(PlayerSession.Role.AirDefender, aor, theatre, startupSoundCompleted: true, isLoadedGame: true);
                    if (LoginScreenUI.instance != null) LoginScreenUI.instance.enabled = false;
                }
                else if (theatre != theatreBefore)
                {
                    try { Bootstrapper.ApplyTheatre(theatre); } catch { }
                    try { FirRegionUtility.InvalidateCache(); } catch { }
                }
                try { File.Delete(path); } catch { }
                WorldGeneration++;
                ClientWorldReady = true;
                Status = "In the host's world";
                CoopLog.Info($"World #{id} loaded in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                error = e.Message;
                Status = "Could not load the host's world: " + e.Message;
                CoopLog.Error("World load failed: " + e);
            }
            finally
            {
                ApplyingRemoteWorld = false;
            }
            CoopSession.Send(MsgType.WorldLoaded, w => { w.I32(id); w.Bool(error == null); w.Str(error ?? ""); });
            if (error == null)
            {
                try { ClientWorldLoaded?.Invoke(); } catch (Exception e) { CoopLog.Error("ClientWorldLoaded: " + e); }
            }
        }

        private static void OnReturnToMenu(NetReader r)
        {
            if (!CoopSession.IsClient) return;
            ClientWorldReady = false;
            Status = "Host returned to the menu";
            CoopLog.Info("Host returned to the main menu");
            if (PlayerSession.IsLoggedIn())
            {
                ClientGate.AllowLocalAction(() => GameResetService.ResetToMainMenu());
            }
        }

        // ---------------------------------------------------------------- helpers

        private static byte[] Compress(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Decompress(byte[] packed)
        {
            using (var input = new MemoryStream(packed))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gz.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
