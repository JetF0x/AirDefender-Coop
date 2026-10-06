using System;
using AirDefenderCoop.Bootstrap;
using AirDefenderCoop.Commands;
using AirDefenderCoop.Puppet;
using AirDefenderCoop.Replication;
using AirDefenderCoop.Diagnostics;
using AirDefenderCoop.Patches;
using AirDefenderCoop.Ui;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "airdefender.coop";
        public const string Name = "Air Defender Co-op";
        public const string Version = "0.2.0";

        public static Plugin Instance { get; private set; }
        internal static Harmony Harmony { get; private set; }

        private bool _cliStarted;

        private void Awake()
        {
            Instance = this;
            CoopConfig.Bind(Config);
            CoopLog.Init(Logger);
            CoopLog.Info($"{Name} {Version} on Unity {Application.unityVersion}, game {CoopSession.GameFingerprint}");

            Harmony = new Harmony(Guid);
            try
            {
                Harmony.PatchAll(typeof(Plugin).Assembly);
                SandboxPatches.Apply(Harmony);
                SimulationGate.Apply(Harmony);
                ReplicationPatches.ApplyScoringGuards(Harmony);
                RouteList.Apply(Harmony);
                PresentationSync.Apply(Harmony);
                RadarHitSync.Apply(Harmony);
            }
            catch (Exception e)
            {
                CoopLog.Error("Patching failed - co-op disabled: " + e);
                enabled = false;
                return;
            }

            WorldSync.Init();
            EntityReplicator.Init();
            TimeSync.Init();
            CommandRouter.Init();
            WorldDigest.Init();
            StateReplicator.Init();
            TrackSync.Init();
            MethodRouter.Init();
            PresentationSync.Init();
            PartnerOverlay.Init();
            CustomGlobals.Init();
            RadarHitSync.Init();
            ReplicationPatches.RegisterCommands();
            Application.runInBackground = true;
            if (CoopConfig.CliAutoTest != null) AutoTest.Start(CoopConfig.CliAutoTest);
        }

        private void Update()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null)
                {
                    if (kb[CoopConfig.PanelInputKey].wasPressedThisFrame) CoopPanel.Toggle();
                    if (kb[UnityEngine.InputSystem.Key.F10].wasPressedThisFrame && kb[UnityEngine.InputSystem.Key.LeftCtrl].isPressed) DevDump.All();
                }
                StartFromCommandLine();
                SteamLobbyService.Tick();
                CoopSession.Tick();
                WorldSync.Tick();
                if (CoopSession.IsHost && CoopSession.Connected)
                {
                    EntityReplicator.HostTick();
                    TimeSync.HostTick();
                    WorldDigest.HostTick();
                    StateReplicator.HostTick();
                    TrackSync.HostTick();
                    RadarHitSync.HostTick();
                    CustomGlobals.HostTick();
                }
                else if (CoopSession.IsClient)
                {
                    EntityReplicator.ClientTick();
                    TrackSync.ClientTick();
                }
                PartnerOverlay.Tick();
                AutoTest.Tick();
            }
            catch (Exception e)
            {
                CoopLog.Error("Update: " + e);
            }
        }

        private void OnGUI()
        {
            try { PartnerOverlay.OnGUI(); CoopPanel.OnGUI(); }
            catch (Exception e) { CoopLog.Error("OnGUI: " + e); }
        }

        private void OnApplicationQuit()
        {
            CoopSession.Stop("game closed");
            SteamLobbyService.Leave();
        }

        private void StartFromCommandLine()
        {
            if (_cliStarted) return;
            if (CoopConfig.CliHostMode == "steam")
            {
                if (!SteamLobbyService.Available) return; // wait for Steam to initialise
                SteamLobbyService.Host();
            }
            else if (CoopConfig.CliHostMode != null) CoopPanel.HostDirect();
            else if (CoopConfig.CliJoinAddress != null) CoopPanel.JoinDirect(CoopConfig.CliJoinAddress);
            _cliStarted = true;
        }
    }
}
