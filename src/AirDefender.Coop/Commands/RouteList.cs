using System;
using AirDefender;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop.Commands
{
    /// <summary>
    /// Every player action the client can take, expressed as the game method that performs it.
    /// The order of entries is the wire id of each route, so it must only ever be appended to.
    /// </summary>
    internal static class RouteList
    {
        public static void Apply(Harmony h)
        {
            var t = typeof(QRAQuickLaunchService);
            // --- QRA launches and tasking
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.LaunchForTrack));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.LaunchForContact));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.LaunchWithOptions));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.LaunchForContactAuto));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.IssueTaskToLaunchGroup));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.IssueRefuelToLaunchGroup));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.IssueRtbToLaunchGroup));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.CascadeSpeedModeToLaunchGroup));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.CancelPendingLaunch));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.WireMintedSortieForAirborneCapEngage));
            MethodRouter.Add(h, t, nameof(QRAQuickLaunchService.TryRestoreRecoveredPairForTasking));

            MethodRouter.Add(h, typeof(ManualQRACommander), nameof(ManualQRACommander.LaunchInterceptors));
            MethodRouter.Add(h, typeof(ManualQRACommander), nameof(ManualQRACommander.LaunchTankerCap));
            MethodRouter.Add(h, typeof(QRACommander), nameof(QRACommander.SetAutoQRAEnabled));
            MethodRouter.Add(h, typeof(QRACommander), nameof(QRACommander.LaunchTankerCap));
            MethodRouter.Add(h, typeof(QRACommander), nameof(QRACommander.MassScrambleAllBases));
            MethodRouter.Add(h, typeof(QRACommander), nameof(QRACommander.MassScrambleAllBasesAgainstTarget));

            // --- Spawning aircraft from bases (UI flows continue with the returned aircraft)
            var b = typeof(Bootstrapper);
            MethodRouter.Add(h, b, nameof(Bootstrapper.SpawnInterceptorAtBase));
            MethodRouter.Add(h, b, nameof(Bootstrapper.SpawnInterceptorAtBaseUnarmedOk));
            MethodRouter.Add(h, b, nameof(Bootstrapper.SpawnMilitaryAsset), blocking: true);
            MethodRouter.Add(h, b, nameof(Bootstrapper.TryRelocateStationedInterceptor));
            MethodRouter.Add(h, b, nameof(Bootstrapper.TryRelocateStationedInterceptorPair));
            MethodRouter.Add(h, b, nameof(Bootstrapper.TryRelocateSupportAsset));
            MethodRouter.Add(h, typeof(AssetsWindowUI), nameof(AssetsWindowUI.TryLaunchInterceptorPair));
            MethodRouter.Add(h, typeof(AirfieldClickHandler), nameof(AirfieldClickHandler.TryLaunchInterceptorPairForEngage));
            MethodRouter.Add(h, typeof(AirfieldClickHandler), nameof(AirfieldClickHandler.TryLaunchInterceptorPairForIdentify));
            MethodRouter.Add(h, typeof(AwacsTargetSelector), nameof(AwacsTargetSelector.SpawnAwacsForTask));
            MethodRouter.Add(h, typeof(NimrodSigintSelector), nameof(NimrodSigintSelector.SpawnNimrodForTask));
            MethodRouter.Add(h, typeof(TankerLaunchSelector), nameof(TankerLaunchSelector.LaunchTanker));

            // --- CAP, support, logistics
            MethodRouter.Add(h, typeof(CAPPairService), nameof(CAPPairService.LaunchCAPPair), new[] { typeof(string), typeof(Vector3), typeof(string) });
            MethodRouter.Add(h, typeof(CAPPairService), nameof(CAPPairService.LaunchCAPPair), new[] { typeof(string), typeof(string), typeof(Vector3), typeof(string) });
            MethodRouter.Add(h, typeof(CAPPairService), nameof(CAPPairService.TryAssignExistingCapGroup));
            MethodRouter.Add(h, typeof(TankerSupportService), nameof(TankerSupportService.EnsureSupportFor));
            MethodRouter.Add(h, typeof(AlternateRecoveryService), nameof(AlternateRecoveryService.TryCommand));
            MethodRouter.Add(h, typeof(HerculesSupplyService), nameof(HerculesSupplyService.TryDispatchCustom),
                new[] { typeof(string), typeof(string), typeof(int), typeof(int), typeof(int), typeof(string).MakeByRefType() });
            MethodRouter.Add(h, typeof(HerculesSupplyService), nameof(HerculesSupplyService.TryDispatchCustom),
                new[] { typeof(string), typeof(string), typeof(string), typeof(int), typeof(int), typeof(int), typeof(string).MakeByRefType() });
            MethodRouter.Add(h, typeof(HerculesSupplyService), nameof(HerculesSupplyService.TryRedeploy));
            MethodRouter.Add(h, typeof(ChinookSupplyService), nameof(ChinookSupplyService.TryDispatch));
            MethodRouter.Add(h, typeof(ChinookSupplyService), nameof(ChinookSupplyService.TryRedeploy));
            MethodRouter.Add(h, typeof(AwacsSelectionService), nameof(AwacsSelectionService.SetActiveAwacs));

            // --- Airspace, SAMs, datalinks
            var a = typeof(AirspaceClosureService);
            MethodRouter.Add(h, a, nameof(AirspaceClosureService.TriggerClosure));
            MethodRouter.Add(h, a, nameof(AirspaceClosureService.ClearClosureFlag));
            MethodRouter.Add(h, a, nameof(AirspaceClosureService.ForceClose));
            MethodRouter.Add(h, a, nameof(AirspaceClosureService.ForceOpen));
            MethodRouter.Add(h, a, nameof(AirspaceClosureService.ReopenAirspace));
            MethodRouter.Add(h, a, nameof(AirspaceClosureService.TryReopenAfterThreatClear));
            MethodRouter.Add(h, typeof(GroundAllAircraftService), nameof(GroundAllAircraftService.GroundAllCivilianAircraft), blocking: false);
            MethodRouter.Add(h, typeof(BloodhoundSamService), nameof(BloodhoundSamService.EnableNetwork));
            MethodRouter.Add(h, typeof(DatalinkL11Manager), nameof(DatalinkL11Manager.SetEnabled));
            MethodRouter.Add(h, typeof(RadarStation), nameof(RadarStation.SetRadarPowered));

            // --- Direct orders to individual aircraft
            var q = typeof(QRAInterceptorMover);
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.RetaskTarget));
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.AssignIdentTask));
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.AssignEngageTask));
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.AssignEscortTask));
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.RequestRefuel), Type.EmptyTypes);
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.RequestRefuel), new[] { typeof(Transform) });
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.RequestStatusUpdate));
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.SetSpeedMode));
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.SetMission));
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.SetTakeoffSlot), new[] { typeof(float) });
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.SetTakeoffSlot), new[] { typeof(float), typeof(bool) });
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.SetFormationSlot));
            MethodRouter.Add(h, q, nameof(QRAInterceptorMover.SetPairAsLeader));

            MethodRouter.Add(h, typeof(AwacsMover), nameof(AwacsMover.RequestRefuel));
            MethodRouter.Add(h, typeof(AwacsMover), nameof(AwacsMover.SetRadarEnabled));
            MethodRouter.Add(h, typeof(AwacsMover), nameof(AwacsMover.SetLoiterAt));
            MethodRouter.Add(h, typeof(AwacsMover), nameof(AwacsMover.ReturnToBase));
            MethodRouter.Add(h, typeof(NimrodSigintMover), nameof(NimrodSigintMover.SetLoiterAt));
            MethodRouter.Add(h, typeof(NimrodSigintMover), nameof(NimrodSigintMover.RequestRefuel));
            MethodRouter.Add(h, typeof(NimrodSigintMover), nameof(NimrodSigintMover.ReturnToBase));
            MethodRouter.Add(h, typeof(OrbitMover), nameof(OrbitMover.SetAsTanker));
            MethodRouter.Add(h, typeof(OrbitMover), nameof(OrbitMover.SetTankerModelType));
            MethodRouter.Add(h, typeof(OrbitMover), nameof(OrbitMover.ReturnToBase), new[] { typeof(Vector3), typeof(bool) });
            MethodRouter.Add(h, typeof(OrbitMover), nameof(OrbitMover.ReturnToBase), new[] { typeof(Vector3), typeof(string), typeof(bool) });
            var tk = typeof(TankerTrackMover);
            MethodRouter.Add(h, tk, nameof(TankerTrackMover.EnableDelayedTakeoff));
            MethodRouter.Add(h, tk, nameof(TankerTrackMover.SetAirborneAltitude));
            MethodRouter.Add(h, tk, nameof(TankerTrackMover.RetaskToNewTrack));
            MethodRouter.Add(h, tk, nameof(TankerTrackMover.SetTrackMetadata));
            MethodRouter.Add(h, tk, nameof(TankerTrackMover.RequestRefuelFromPlayer));
            MethodRouter.Add(h, tk, nameof(TankerTrackMover.SetRacetrackWidthNm));
            MethodRouter.Add(h, tk, nameof(TankerTrackMover.SetLaunchBaseName));
            MethodRouter.Add(h, tk, nameof(TankerTrackMover.ReturnToBase), new[] { typeof(Vector3), typeof(string), typeof(bool) });
            MethodRouter.Add(h, tk, nameof(TankerTrackMover.ReturnToBase), new[] { typeof(Vector3), typeof(bool) });
            MethodRouter.Add(h, typeof(AircraftAltitudeManager), nameof(AircraftAltitudeManager.SetTargetAltitude));

            CoopLog.Info($"Command routes: {MethodRouter.RouteCount}");
        }
    }
}
