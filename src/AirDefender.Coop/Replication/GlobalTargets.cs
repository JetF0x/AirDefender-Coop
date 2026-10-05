using System;
using System.Collections.Generic;
using System.Linq;
using AirDefender;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Game-wide state outside the contacts that the client must mirror: scores, base stocks,
    /// casualties, campaign progress, destroyed features, radar power, track serials.
    /// </summary>
    internal sealed class GlobalTarget
    {
        public string Key;
        public Type Type;
        public bool IsStatic;
        public Func<object> Instance;
        public Action AfterApply;
    }

    internal static class GlobalTargets
    {
        private static List<GlobalTarget> _all;
        private static readonly Dictionary<Type, UnityEngine.Object> SingletonCache = new Dictionary<Type, UnityEngine.Object>();

        /// <summary>Field allowlists for targets where most fields are local presentation.</summary>
        internal static readonly Dictionary<Type, HashSet<string>> Allow = new Dictionary<Type, HashSet<string>>
        {
            [typeof(RadarStation)] = new HashSet<string> { "radarPowered", "cycleState", "sweepSuppressed", "lockedStationIds", "fylingdalesUnlocked" },
            [typeof(MapFeaturesRenderer)] = new HashSet<string> { "destroyedFeatureIds" },
            [typeof(TrackManager)] = new HashSet<string>
            {
                "contactIdToSerial", "nextSerialId", "ewRevealedAircraftTypes", "visuallyIdentifiedAircraftTypes",
                "enemyInsideFirPenaltyApplied", "raidWarnedThroughLadder", "pendingUnknownByEw", "pendingHostileByEw",
                "pendingHostileByWeaponIntel", "nextQraSuffixOctal",
            },
        };

        public static IEnumerable<GlobalTarget> All
        {
            get
            {
                if (_all == null) _all = Build();
                foreach (var t in _all) yield return t;
                // Radar stations are rebuilt per theatre, so enumerate them live.
                if (RadarStation.registry == null) yield break;
                foreach (var st in RadarStation.registry.ToArray())
                {
                    if (st == null || string.IsNullOrEmpty(st.stationName)) continue;
                    var captured = st;
                    yield return new GlobalTarget { Key = "Radar:" + st.stationName, Type = typeof(RadarStation), Instance = () => captured };
                }
            }
        }

        public static GlobalTarget Find(string key) => All.FirstOrDefault(t => t.Key == key);

        private static List<GlobalTarget> Build()
        {
            var l = new List<GlobalTarget>();
            void Static(Type t) => l.Add(new GlobalTarget { Key = t.Name + ".static", Type = t, IsStatic = true, Instance = () => null });
            void Singleton(Type t, bool withStatics = false)
            {
                l.Add(new GlobalTarget { Key = t.Name, Type = t, Instance = () => FindSingleton(t) });
                if (withStatics) Static(t);
            }

            l.Add(new GlobalTarget { Key = "Scoring", Type = typeof(TriStateScoringServiceImpl), Instance = () => TriStateScoringService._impl });
            Singleton(typeof(AirfieldFuelManager));
            Singleton(typeof(AirfieldWeaponsManager), withStatics: true);
            Singleton(typeof(CasualtyService));
            Singleton(typeof(EnemyCampaignDirector));
            Singleton(typeof(CriticalRaidOrchestrator), withStatics: true);
            Singleton(typeof(BloodhoundSamService));
            Singleton(typeof(EnemyIntelligenceService));
            Singleton(typeof(MapFeaturesRenderer));
            Singleton(typeof(TrackManager));
            Static(typeof(CampaignVictoryTracker));
            Static(typeof(DestroyedAirfieldService));
            Static(typeof(AssetInventory));
            Static(typeof(AirspaceClosureService));
            Static(typeof(QRACommander));
            Static(typeof(SessionStatisticsService));
            Static(typeof(RadarStation));
            return l;
        }

        private static UnityEngine.Object FindSingleton(Type t)
        {
            if (SingletonCache.TryGetValue(t, out var o) && o != null) return o;
            o = UnityEngine.Object.FindAnyObjectByType(t);
            SingletonCache[t] = o;
            return o;
        }

        public static bool IsAllowed(Type owner, string field)
        {
            return !Allow.TryGetValue(owner, out var set) || set.Contains(field);
        }
    }
}
