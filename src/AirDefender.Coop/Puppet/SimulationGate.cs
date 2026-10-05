using System;
using System.Collections.Generic;
using System.Reflection;
using AirDefender;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop.Puppet
{
    /// <summary>
    /// Client puppet mode: while the client mirrors the host, every system that makes simulation
    /// decisions (spawning, AI, movement, weapons, scoring, scenario timing) is switched off so the
    /// only source of change is the host. Presentation (UI, radar sweep, map) keeps running.
    /// </summary>
    internal static class SimulationGate
    {
        /// <summary>Per-entity logic components. Their transforms/fields are driven by replication.</summary>
        internal static readonly string[] EntityLogicTypes =
        {
            "AircraftAltitudeManager", "AirToAirMissileMover", "AirToGroundMissileMover", "AirwayFollowerMover",
            "AwacsMover", "BakedRouteFollowerMover", "BallisticLaunchMover", "CAPPairController",
            "CivilianTrafficController", "DistanceTravelMissileLauncher", "EnemyAircraftController",
            "EnemyAltitudeController", "EnemyFighterAI", "EnemyFighterEscortMover", "EnemyMovementController",
            "EnemyTankerMover", "EnemyWeaponController", "IfrApproachController", "IfrDepartureSpeedController",
            "InterceptorOnboardRadar", "JammingCapability", "LegSpeedController", "MirvWarheadMover",
            "NimrodSigintMover", "OrbitMover", "QRAEscortMover", "QRAInterceptorMover", "RadarCircleMover",
            "RigHelicopterShuttleController", "SeadAircraftMover", "TakeoffTurnMover", "TankerSupportMover",
            "TankerTrackMover", "TridentMissileMover", "VfrCrossCountryController", "VfrLocalSortieController",
            "WaypointPathMover", "ForcePendingTag", "MissionTargetController", "NimrodEwEmitter",
            "LandingAircraftMarker", "ReindeerAttackController", "SantaTrackDatalinkFeed",
        };

        /// <summary>World-level simulation drivers (spawners, directors, schedulers).</summary>
        internal static readonly string[] WorldSimTypes =
        {
            "AirfieldArrivalSequencer", "AirfieldDepartureSequencer", "ArmageddonGameEndService",
            "BloodhoundSamService", "Bootstrapper", "ChinookSupplyService", "CriticalBallisticLaunchDirector",
            "CriticalRaidOrchestrator", "EnemyCampaignDirector", "EnemyIntelligenceService", "EnemyMissionScheduler",
            "F3Spawner", "Failures.FailureManager", "Fr24LiveTrafficService", "HerculesSupplyService",
            "HistoricalLaunchScheduler", "IdoDayOneManager", "IdoIfrTrafficSpawner", "IdoVfrTrafficSpawner",
            "RadarDeceptiveJammingInjector", "RigHelicopterShuttleSpawner", "ScenarioDirector", "ScenarioRunner",
        };

        /// <summary>Owners of TickManager callbacks that simulate rather than present.</summary>
        internal static readonly string[] TickSimTypes =
        {
            "RadarCoverageDespawn", "ManifestEnforcer", "IfrSeparationMonitor", "TensionStateWatcher",
            "SantaEventDirector", "DatalinkL11Manager", "ForceCallsignSetter", "CivilianLandingEscortService",
            "GlobalInterceptorMonitor",
        };

        private static readonly HashSet<Type> TickSim = new HashSet<Type>();
        private static readonly string[] FrameMethods = { "Update", "LateUpdate", "FixedUpdate" };

        public static int PatchedMethods { get; private set; }

        public static void Apply(Harmony h)
        {
            Assembly game = typeof(TrackManager).Assembly;
            var prefix = new HarmonyMethod(typeof(SimulationGate), nameof(SkipOnClient));
            foreach (var name in Concat(EntityLogicTypes, WorldSimTypes))
            {
                Type t = game.GetType("AirDefender." + name);
                if (t == null) { CoopLog.Warn($"SimulationGate: type {name} not found"); continue; }
                foreach (var m in FrameMethods)
                {
                    var mi = AccessTools.DeclaredMethod(t, m, Type.EmptyTypes);
                    if (mi == null) continue;
                    h.Patch(mi, prefix: prefix);
                    PatchedMethods++;
                }
            }
            foreach (var name in TickSimTypes)
            {
                Type t = game.GetType("AirDefender." + name);
                if (t != null) TickSim.Add(t);
                else CoopLog.Warn($"SimulationGate: tick type {name} not found");
            }
            h.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.Register)),
                prefix: new HarmonyMethod(typeof(SimulationGate), nameof(WrapTick)));
            CoopLog.Info($"SimulationGate: {PatchedMethods} frame methods gated, {TickSim.Count} tick owners");
        }

        private static IEnumerable<string> Concat(string[] a, string[] b)
        {
            foreach (var x in a) yield return x;
            foreach (var x in b) yield return x;
        }

        private static bool SkipOnClient() => !ClientGate.Suppress;

        private static void WrapTick(MonoBehaviour target, ref Action callback)
        {
            if (target == null || callback == null || !TickSim.Contains(target.GetType())) return;
            Action inner = callback;
            callback = () => { if (!ClientGate.Suppress) inner(); };
        }
    }
}
