using System;
using System.Linq;
using AirDefender;
using AirDefenderCoop.Puppet;
using AirDefenderCoop.Replication;
using UnityEngine;

namespace AirDefenderCoop.Diagnostics
{
    /// <summary>
    /// "host-events" / "client-events": presentation and panel state that only the host's
    /// simulation produces. The client starts a supply flight, a QRA launch and a TRG sortie and
    /// checks its panels' data; the host then raises a nuclear blast, a friendly loss and a victory,
    /// and the client checks it saw each one and continues the game from the debrief.
    /// </summary>
    public static partial class AutoTest
    {
        private const string TestLossName = "AUTOTEST JET";

        private static void AddHostEventSteps()
        {
            _steps.Add(new Step { Name = "host panel state", Delay = 30f, Run = () => DumpPanelState("host") });
            // The launch's radio sequence waits for player responses an autotest never gives, so
            // stand in for its end: the countdown starts as the game's own sequence would start it.
            _steps.Add(new Step
            {
                Name = "host starts the pending QRA countdown", Timeout = 60f,
                Ready = () => QRAQuickLaunchService.s_pendingOrProcessingTargets.Count > 0,
                Run = () =>
                {
                    foreach (var t in QRAQuickLaunchService.s_pendingOrProcessingTargets.ToList())
                        if (!QRAQuickLaunchService.TryGetPendingLaunchEta(t, out _, out _))
                            QRAQuickLaunchService.SetPendingLaunchEta(t, GameTimeline.GetTimelineSecondsFloat() + 245f, true);
                },
            });
            _steps.Add(new Step
            {
                Name = "host nuclear blast", Delay = 60f,
                Run = () => NuclearBlastEffect.Spawn(new Vector3(-40f, 40f, 0f), 30f),
            });
            _steps.Add(new Step
            {
                Name = "host friendly loss and explosion (as simulation)", Delay = 3f,
                Run = () => SimulationGate.RunAsSimulation(() =>
                {
                    FriendlyLossFeedback.OnFriendlyShotDown(TestLossName, "shot down in an autotest");
                    SfxService.PlayConventionalExplosion();
                }),
            });
            _steps.Add(new Step { Name = "host panel state 2", Delay = 12f, Run = () => DumpPanelState("host") });
            _steps.Add(new Step
            {
                Name = "host victory", Delay = 3f,
                Run = ArmageddonGameEndService.TriggerVictory,
            });
            _steps.Add(new Step
            {
                Name = "host continued by the client", Timeout = 60f,
                Ready = () => { var ui = UnityEngine.Object.FindAnyObjectByType<ArmageddonEndGameUI>(); return ui != null && !ui.isVisible && Time.timeScale > 0f; },
                Run = () => CoopLog.Info("[AUTOTEST] PASS client's Continue resumed the host's game"),
            });
        }

        private static void AddClientEventChecks()
        {
            _steps.Add(new Step { Name = "wait for host world", Ready = () => ClientGate.PuppetActive, Delay = 8f, Timeout = 180f });

            // Supply flight list.
            _steps.Add(new Step
            {
                Name = "client dispatches a Hercules",
                Run = () =>
                {
                    var bases = Bootstrapper.s_manifestEntriesByBase?.Keys.Where(b => !string.IsNullOrEmpty(b)).Take(2).ToList();
                    if (bases == null || bases.Count < 2) throw new Exception("need two bases");
                    bool ok = HerculesSupplyService.TryDispatchCustom(bases[0], bases[1], 2, 0, 0, out string error);
                    CoopLog.Info($"[AUTOTEST] Hercules {bases[0]} -> {bases[1]}: {(ok ? "dispatched" : "refused: " + error)}");
                },
            });
            _steps.Add(new Step
            {
                Name = "supply flight listed on client", Timeout = 15f,
                Ready = () => HerculesSupplyService.GetActiveFlights().Count > 0,
                Run = () => CoopLog.Info($"[AUTOTEST] PASS supply flight list ({HerculesSupplyService.GetActiveFlights().Count} Hercules, status '{HerculesSupplyService.GetFlightStatusForUi(HerculesSupplyService.GetActiveFlights()[0])}')"),
            });

            // The QRA launch-options dialog opens on the requesting player's own screen.
            _steps.Add(new Step
            {
                Name = "client opens the QRA dialog", Ready = () => PickIdentTarget() != null, Timeout = 60f,
                Run = () =>
                {
                    string target = PickIdentTarget();
                    var tr = TrackManager.GetTrackTransform(target);
                    QRAQuickLaunchService.LaunchForContact(target, tr != null ? tr.position : Vector3.zero);
                    Report(QRALaunchOptionsUI.IsVisible, $"QRA launch-options dialog opened locally for {target}");
                    QRALaunchOptionsUI.Hide();
                },
            });

            // Pending QRA countdown (the launch the QRA dialog ends in when no base is picked).
            _steps.Add(new Step
            {
                Name = "client launches QRA against a track", Ready = () => PickIdentTarget() != null, Timeout = 60f,
                Run = () =>
                {
                    _identTarget = PickIdentTarget();
                    var tr = TrackManager.GetTrackTransform(_identTarget);
                    QRAQuickLaunchService.LaunchForContactAuto(_identTarget, tr != null ? tr.position : Vector3.zero);
                    CoopLog.Info($"[AUTOTEST] QRA launch requested against {_identTarget}");
                },
            });
            _steps.Add(new Step
            {
                // The ETA is set once the launch's opening radio calls finish.
                Name = "pending QRA on client", Timeout = 60f,
                Ready = () => QRAQuickLaunchService.IsLaunchPendingOrCommittedForTarget(_identTarget),
                Run = () =>
                {
                    bool eta = QRAQuickLaunchService.TryGetPendingLaunchEta(_identTarget, out float at, out bool timeline);
                    float now = timeline ? GameTimeline.GetTimelineSecondsFloat() : Time.unscaledTime;
                    CoopLog.Info($"[AUTOTEST] PASS QRA pending for {_identTarget} on client" + (eta ? $", T-{at - now:0} s ({(timeline ? "timeline" : "unscaled")})" : ", no ETA yet"));
                },
            });
            _steps.Add(new Step
            {
                Name = "pending QRA countdown on client", Timeout = 90f,
                Ready = () => QRAQuickLaunchService.TryGetPendingLaunchEta(_identTarget, out _, out _) || QRAQuickLaunchService.s_targetsLaunched.Contains(_identTarget),
                Run = () =>
                {
                    bool eta = QRAQuickLaunchService.TryGetPendingLaunchEta(_identTarget, out float at, out bool timeline);
                    float now = timeline ? GameTimeline.GetTimelineSecondsFloat() : Time.unscaledTime;
                    CoopLog.Info(eta ? $"[AUTOTEST] PASS QRA countdown T-{at - now:0} s ({(timeline ? "timeline" : "unscaled")})"
                                     : "[AUTOTEST] PASS QRA already launched (countdown over)");
                },
            });

            // TRG sortie from a base: its radio channel carries the training-area label.
            _steps.Add(new Step
            {
                Name = "client launches a TRG pair", Delay = 1f,
                Run = () =>
                {
                    string baseName = FindBaseWith("F3");
                    var area = AdTrainingAreaCatalog.Areas.FirstOrDefault();
                    if (baseName == null || area == null) throw new Exception("no F3 base or AD area");
                    // A null type takes any combat aircraft at the base, as the base's TRG button does.
                    bool ok = TrgSortieService.TryLaunchFromBase(baseName, null, area.DeepestPoint, 1, out _, out string reason);
                    CoopLog.Info($"[AUTOTEST] TRG from {baseName} to {area.Label}: {(ok ? "tasked" : "refused: " + reason)}");
                },
            });
            _steps.Add(new Step
            {
                Name = "TRG channel on client", Timeout = 30f,
                Ready = () => RadioChannelUI.instance != null && RadioChannelUI.instance.channels.Any(c => c?.displayLabel != null && c.displayLabel.StartsWith("TRG")),
                Run = () =>
                {
                    var c = RadioChannelUI.instance.channels.First(x => x?.displayLabel != null && x.displayLabel.StartsWith("TRG"));
                    CoopLog.Info($"[AUTOTEST] PASS TRG channel {c.channelNumber} '{c.displayLabel}' ({RadioChannelUI.instance.channels.Count} channels)");
                },
            });

            _steps.Add(new Step { Name = "client panel state", Run = () => DumpPanelState("client") });

            // Host-side events, counted as they arrive.
            _steps.Add(new Step
            {
                Name = "victory debrief on client", Timeout = 180f,
                Ready = () => { var ui = UnityEngine.Object.FindAnyObjectByType<ArmageddonEndGameUI>(); return ui != null && ui.isVisible && ui.currentState == ArmageddonEndGameUI.EndState.Victory; },
                Run = () =>
                {
                    CoopLog.Info("[AUTOTEST] PASS victory debrief shown");
                    Report(PresentationSync.NukesApplied > 0 && UnityEngine.Object.FindAnyObjectByType<NuclearBlastEffect>() != null, $"nuclear blast effect ({PresentationSync.NukesApplied})");
                    Report(PresentationSync.BannersApplied > 0, $"alert banners from the host's simulation ({PresentationSync.BannersApplied})");
                    Report(PresentationSync.MusicApplied > 0, $"music cues ({PresentationSync.MusicApplied})");
                    Report(PresentationSync.SfxApplied > 0, $"simulation sounds ({PresentationSync.SfxApplied})");
                },
            });
            _steps.Add(new Step
            {
                Name = "client presses Continue", Delay = 3f,
                Run = () => UnityEngine.Object.FindAnyObjectByType<ArmageddonEndGameUI>().ContinuePlaying(),
            });
            _steps.Add(new Step
            {
                Name = "debrief closed by host", Timeout = 15f,
                Ready = () => { var ui = UnityEngine.Object.FindAnyObjectByType<ArmageddonEndGameUI>(); return ui != null && !ui.isVisible && Time.timeScale > 0f; },
                Run = () => CoopLog.Info("[AUTOTEST] PASS debrief closed and game resumed for everyone"),
            });
        }

        private static void DumpPanelState(string who)
        {
            var ui = RadioChannelUI.instance;
            string ch = ui == null ? "no RadioChannelUI" : string.Join("; ", ui.channels.Where(c => c != null).Select(c => $"{c.channelNumber}:{c.sortieKey}:'{c.displayLabel}':{c.leadContactId}/{c.wingContactId}"));
            CoopLog.Info($"[AUTOTEST] {who} radio channels: {ch}");
            string eta = string.Join("; ", QRAQuickLaunchService.s_pendingLaunchInfoByTarget.Select(kv => $"{kv.Key}@{kv.Value.launchAt:0}{(kv.Value.usesTimeline ? "t" : "u")}"));
            CoopLog.Info($"[AUTOTEST] {who} QRA pending=[{string.Join(",", QRAQuickLaunchService.s_pendingOrProcessingTargets)}] launched=[{string.Join(",", QRAQuickLaunchService.s_targetsLaunched)}] eta=[{eta}] timeline={GameTimeline.GetTimelineSecondsFloat():0}");
        }

        private static void Report(bool ok, string what) => CoopLog.Info($"[AUTOTEST] {(ok ? "PASS" : "FAIL")} {what}");
    }
}
