using AirDefender;
using AirDefenderCoop.Commands;
using AirDefenderCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// The end-of-game debrief (victory, Armageddon defeat, the Santa event's defeat). The host's
    /// gated ArmageddonGameEndService decides the game is over; every client shows the same screen
    /// from its own replicated casualties and destroyed features. "Continue playing" after a victory
    /// is the host's decision for everyone; a client pressing it asks the host.
    /// </summary>
    [HarmonyPatch]
    internal static class EndGameSync
    {
        private enum Kind : byte { Victory = 1, Defeat = 2, SantaDefeat = 3, Continue = 4, Hide = 5 }

        private static Kind _hostShown;   // what the host's screen shows now (0 = nothing), for late joiners

        public static void Init()
        {
            CoopSession.Register(MsgType.EndGame, OnEndGame);
            CommandRouter.Register("EndContinue", _ => HostContinue());
            Bootstrap.WorldSync.PartnerContactsReported += (peer, __) =>
            {
                if (CoopSession.IsHost && _hostShown != 0) CoopSession.SendTo(peer, MsgType.EndGame, w => w.U8((byte)_hostShown));
            };
        }

        private static bool HostSending => CoopSession.IsHost && CoopSession.Connected;

        private static void Broadcast(Kind k)
        {
            if (!HostSending) return;
            CoopSession.Send(MsgType.EndGame, w => w.U8((byte)k));
        }

        // ---------------------------------------------------------------- host

        [HarmonyPatch(typeof(ArmageddonGameEndService), nameof(ArmageddonGameEndService.ShowEndGameUI))]
        [HarmonyPostfix]
        private static void AfterShow(bool victory)
        {
            if (!CoopSession.IsHost) return;
            _hostShown = victory ? Kind.Victory : Kind.Defeat;
            Broadcast(_hostShown);
        }

        [HarmonyPatch(typeof(ArmageddonEndGameUI), nameof(ArmageddonEndGameUI.DisplaySantaDefeat))]
        [HarmonyPostfix]
        private static void AfterSanta()
        {
            if (!CoopSession.IsHost) return;
            _hostShown = Kind.SantaDefeat;
            Broadcast(_hostShown);
        }

        [HarmonyPatch(typeof(ArmageddonEndGameUI), nameof(ArmageddonEndGameUI.ContinuePlaying))]
        [HarmonyPrefix]
        private static bool BeforeContinue()
        {
            // Client: the shared game continues only when the host says so.
            if (CoopSession.IsClient && CommandRouter.Route("EndContinue", null)) return false;
            return true;
        }

        [HarmonyPatch(typeof(ArmageddonEndGameUI), nameof(ArmageddonEndGameUI.ContinuePlaying))]
        [HarmonyPostfix]
        private static void AfterContinue()
        {
            if (!CoopSession.IsHost) return;
            _hostShown = 0;
            Broadcast(Kind.Continue);
        }

        // The host leaving the debrief for the menu: clients close theirs and follow the host out.
        [HarmonyPatch(typeof(ArmageddonEndGameUI), nameof(ArmageddonEndGameUI.ReturnToMenu))]
        [HarmonyPrefix]
        private static void BeforeReturnToMenu() => HostLeft();

        [HarmonyPatch(typeof(ArmageddonEndGameUI), nameof(ArmageddonEndGameUI.TryAgain))]
        [HarmonyPrefix]
        private static void BeforeTryAgain() => HostLeft();

        [HarmonyPatch(typeof(ArmageddonEndGameUI), nameof(ArmageddonEndGameUI.NavigateToTradeTraining))]
        [HarmonyPrefix]
        private static void BeforeTraining() => HostLeft();

        private static void HostLeft()
        {
            if (!CoopSession.IsHost) return;
            _hostShown = 0;
            Broadcast(Kind.Hide);
        }

        private static void HostContinue()
        {
            var ui = Object.FindAnyObjectByType<ArmageddonEndGameUI>();
            if (ui != null && ui.isVisible && ui.currentState == ArmageddonEndGameUI.EndState.Victory) ui.ContinuePlaying();
        }

        // ---------------------------------------------------------------- client

        private static void OnEndGame(NetReader r)
        {
            var kind = (Kind)r.U8();
            if (!ClientGate.PuppetActive) return;
            ClientGate.AllowLocalAction(() =>
            {
                switch (kind)
                {
                    case Kind.Victory:
                    case Kind.Defeat: Show(kind == Kind.Victory); break;
                    case Kind.SantaDefeat: ArmageddonEndGameUI.DisplaySantaDefeat(); break;
                    case Kind.Continue:
                    {
                        var ui = Object.FindAnyObjectByType<ArmageddonEndGameUI>();
                        if (ui != null && ui.isVisible) ui.ContinuePlaying(); else ArmageddonGameEndService.EnterEndlessMode();
                        break;
                    }
                    case Kind.Hide:
                    {
                        var ui = Object.FindAnyObjectByType<ArmageddonEndGameUI>();
                        if (ui == null || !ui.isVisible) break;
                        ui.Hide();
                        try { SfxService.ResumeAllAudio(); SfxService.StopSoundtrack(0.5f); } catch { }
                        break;
                    }
                }
            });
        }

        // Mirrors the end of ArmageddonGameEndService.TriggerVictory / EndGameWithDefeat.
        private static void Show(bool victory)
        {
            var svc = ArmageddonGameEndService.instance;
            if (svc == null) { CoopLog.Warn("End of game: no ArmageddonGameEndService on this client"); return; }
            if (svc.gameEnded && svc.victoryAchieved == victory) return;
            svc.gameEnded = true;
            svc.victoryAchieved = victory;
            if (!victory) svc.armageddonTriggered = true;
            svc.CacheDestroyedFeatures();
            svc.ShowEndGameUI(victory);
            CoopLog.Info($"End of game shown: {(victory ? "victory" : "defeat")}");
        }
    }
}
