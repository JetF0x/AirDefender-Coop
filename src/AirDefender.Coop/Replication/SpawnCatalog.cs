using System;
using System.Collections.Generic;
using AirDefender;
using UnityEngine;
using GSM = AirDefender.GameSaveManager;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Spawn records reuse the game's own save format: the host captures the entity's save-state
    /// entry and the client rebuilds the entity with the matching restore routine, so a replicated
    /// aircraft is constructed exactly as a loaded one would be.
    /// </summary>
    internal static class SpawnCatalog
    {
        public enum Kind : byte { Civil = 1, Vfr = 2, Enemy = 3, Interceptor = 4, Missile = 5, CapAnchor = 6, Stationed = 7, Awacs = 8, Nimrod = 9, Orbit = 10, Tanker = 11, Clone = 20 }

        public struct Record
        {
            public Kind Kind;
            public string Id;
            public string Json;
        }

        /// <summary>Host: index every per-entity state in a fresh snapshot by contact id.</summary>
        public static Dictionary<string, Record> Capture()
        {
            var map = new Dictionary<string, Record>(StringComparer.Ordinal);
            GSM.SaveBlob blob = GSM.BuildSnapshot();
            void Add<T>(Kind k, string id, T state)
            {
                if (string.IsNullOrEmpty(id) || state == null || map.ContainsKey(id)) return;
                map[id] = new Record { Kind = k, Id = id, Json = JsonUtility.ToJson(state) };
            }
            if (blob.civilianAircraft != null) foreach (var s in blob.civilianAircraft) if (s != null) Add(Kind.Civil, s.contactId, s);
            if (blob.vfrAircraft != null) foreach (var s in blob.vfrAircraft) if (s != null) Add(Kind.Vfr, s.contactId, s);
            if (blob.activeEnemies != null) foreach (var s in blob.activeEnemies) Add(Kind.Enemy, s.contactId, s);
            if (blob.activeInterceptors != null) foreach (var s in blob.activeInterceptors) if (s != null) Add(Kind.Interceptor, s.id, s);
            if (blob.inFlightMissiles != null) foreach (var s in blob.inFlightMissiles) if (s != null) Add(Kind.Missile, s.missileId, s);
            if (blob.capAnchors != null) foreach (var s in blob.capAnchors) if (s != null) Add(Kind.CapAnchor, s.contactId, s);
            if (blob.stationedMilitary != null) foreach (var s in blob.stationedMilitary) if (s != null) Add(Kind.Stationed, s.id, s);
            if (blob.parkedAssets != null)
            {
                // Airborne/parked support aircraft are embedded as JSON states under marker base names.
                foreach (var p in blob.parkedAssets)
                {
                    if (p == null || string.IsNullOrEmpty(p.assetTypeName)) continue;
                    try
                    {
                        switch (p.baseName)
                        {
                            case "__AWACS_STATE__": AddRaw(map, Kind.Awacs, JsonUtility.FromJson<AwacsMover.SavedState>(p.assetTypeName).id, p.assetTypeName); break;
                            case "__SIGINT_STATE__": AddRaw(map, Kind.Nimrod, JsonUtility.FromJson<NimrodSigintMover.SavedState>(p.assetTypeName).contactId, p.assetTypeName); break;
                            case "__ORBIT_STATE__": AddRaw(map, Kind.Orbit, JsonUtility.FromJson<OrbitMover.SavedState>(p.assetTypeName).id, p.assetTypeName); break;
                            case "__TANKER_TRACK_STATE__": AddRaw(map, Kind.Tanker, JsonUtility.FromJson<TankerTrackMover.SavedState>(p.assetTypeName).id, p.assetTypeName); break;
                        }
                    }
                    catch (Exception e) { CoopLog.Debug("parked state parse: " + e.Message); }
                }
            }
            return map;
        }

        private static void AddRaw(Dictionary<string, Record> map, Kind k, string id, string json)
        {
            if (string.IsNullOrEmpty(id) || map.ContainsKey(id)) return;
            map[id] = new Record { Kind = k, Id = id, Json = json };
        }

        /// <summary>Host: a structural copy for contacts the save format does not describe.</summary>
        public static Record Clone(IContact c)
        {
            var mb = (MonoBehaviour)c;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            var p = mb.transform.position;
            sb.Append(p.x.ToString("R", ci)).Append(';').Append(p.y.ToString("R", ci)).Append(';')
              .Append(p.z.ToString("R", ci)).Append(';').Append(mb.transform.eulerAngles.z.ToString("R", ci));
            foreach (var comp in mb.GetComponents<Component>())
            {
                if (comp == null || comp is Transform || comp is CoopPuppet) continue;
                sb.Append('|').Append(comp.GetType().AssemblyQualifiedName);
            }
            return new Record { Kind = Kind.Clone, Id = c.ContactId, Json = sb.ToString() };
        }

        public static bool ApplyClone(string id, string payload)
        {
            var parts = payload.Split('|');
            var head = parts[0].Split(';');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var go = new GameObject(id);
            go.SetActive(false);
            go.transform.position = new Vector3(float.Parse(head[0], ci), float.Parse(head[1], ci), float.Parse(head[2], ci));
            go.transform.rotation = Quaternion.Euler(0f, 0f, float.Parse(head[3], ci));
            for (int i = 1; i < parts.Length; i++)
            {
                var t = Type.GetType(parts[i]);
                if (t == null) { CoopLog.Warn($"Clone {id}: unknown component {parts[i]}"); continue; }
                if (go.GetComponent(t) != null) continue; // already added via [RequireComponent]
                try { go.AddComponent(t); }
                catch (Exception e) { CoopLog.Warn($"Clone {id}: cannot add {t.Name}: {e.Message}"); }
            }
            // Give contact components their id before they wake up.
            foreach (var comp in go.GetComponents<MonoBehaviour>())
            {
                if (!(comp is IContact)) continue;
                var f = comp.GetType().GetField("contactId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(string)) f.SetValue(comp, id);
            }
            go.SetActive(true);
            foreach (var comp in go.GetComponents<MonoBehaviour>())
                if (comp is IContact ic) ContactRegistry.Register(ic);
            return true;
        }

        private static GameObject NewAircraftObject(string id, string baseName, string assetType)
        {
            var go = new GameObject(id);
            try { go.AddComponent<ManifestTag>().Configure(baseName, assetType); } catch { }
            return go;
        }

        // Mirrors one entry of GameSaveManager.RestoreStationedMilitary (which first deletes every stationed aircraft).
        private static bool RestoreStationed(GSM.MilitaryStationedAsset m)
        {
            if (m == null || string.IsNullOrEmpty(m.baseName) || string.IsNullOrEmpty(m.aircraftTypeName)) return false;
            string n = m.aircraftTypeName;
            string assetType = n.Contains("F3") ? "F3" : n.Contains("GR1") ? "GR1" : n.Contains("Jaguar") ? "JAG" : n.Contains("Harrier") ? "HAR"
                : n.Contains("Hawk") ? "HAWK" : n.Contains("Lightning") ? "LIGHTNINGF6" : n.Contains("Phantom") ? "PHANTOMFGR2" : "INT";
            var go = new GameObject(string.IsNullOrEmpty(m.id) ? n + "_" + m.baseName : m.id);
            go.transform.position = m.baseWorld;
            go.AddComponent<ManifestTag>().Configure(assetType: assetType, baseName: m.baseName);
            var q = go.AddComponent<QRAInterceptorMover>();
            q.SetAircraftTypeName(n);
            q.ConfigureWithId(go.name, "STATIONED", m.baseWorld);
            if (m.skyflash + m.sidewinder > 0)
                q.SetWeaponsAndFuel(Mathf.Max(0, m.skyflash), Mathf.Max(0, m.sidewinder), Mathf.Max(0, m.cannonRounds), Mathf.Max(0f, m.fuelKg));
            else
                q.SetWeaponsAndFuel(Mathf.Max(0, m.missiles), Mathf.Max(0, m.cannonRounds), Mathf.Max(0f, m.fuelKg));
            return true;
        }

        /// <summary>Client: build the entity. Returns false when the record could not be applied.</summary>
        public static bool Apply(Kind kind, string json)
        {
            switch (kind)
            {
                case Kind.Civil:
                    GSM.RestoreCivilianAircraft(new List<GSM.CivilianAircraftState> { JsonUtility.FromJson<GSM.CivilianAircraftState>(json) });
                    return true;
                case Kind.Vfr:
                    GSM.RestoreVFRAircraft(new List<GSM.VFRAircraftState> { JsonUtility.FromJson<GSM.VFRAircraftState>(json) });
                    return true;
                case Kind.Enemy:
                    GSM.RestoreActiveEnemies(new List<EnemyAircraftController.SavedState> { JsonUtility.FromJson<EnemyAircraftController.SavedState>(json) });
                    return true;
                case Kind.Interceptor:
                    GSM.RestoreActiveInterceptors(new List<GSM.ActiveInterceptorState> { JsonUtility.FromJson<GSM.ActiveInterceptorState>(json) });
                    return true;
                case Kind.CapAnchor:
                    var cap = JsonUtility.FromJson<GSM.CAPAnchorState>(json);
                    return GSM.EnsureCapAnchorExists(cap.contactId, cap.position);
                case Kind.Missile:
                    return RestoreMissile(JsonUtility.FromJson<GSM.InFlightMissileState>(json));
                case Kind.Stationed:
                    return RestoreStationed(JsonUtility.FromJson<GSM.MilitaryStationedAsset>(json));
                case Kind.Awacs:
                {
                    var st = JsonUtility.FromJson<AwacsMover.SavedState>(json);
                    var go = NewAircraftObject(st.id, GSM.ResolveNearestAirfieldName(st.basePosition != Vector3.zero ? st.basePosition : st.position), "AWACS");
                    go.AddComponent<AwacsMover>().ImportState(st);
                    return true;
                }
                case Kind.Nimrod:
                {
                    var st = JsonUtility.FromJson<NimrodSigintMover.SavedState>(json);
                    var go = NewAircraftObject(st.contactId, !string.IsNullOrEmpty(st.baseName) ? st.baseName : GSM.ResolveNearestAirfieldName(st.position), "Nimrod");
                    var m = go.AddComponent<NimrodSigintMover>();
                    go.AddComponent<NimrodEwEmitter>();
                    try { go.AddComponent<FixedCallsignTag>().SetCallsign(NimrodSigintMover.BuildCallsign(st.contactId)); } catch { }
                    m.ImportState(st);
                    return true;
                }
                case Kind.Orbit:
                {
                    var st = JsonUtility.FromJson<OrbitMover.SavedState>(json);
                    string b = !string.IsNullOrEmpty(st.manifestBaseName) ? st.manifestBaseName : !string.IsNullOrEmpty(st.rtbBaseName) ? st.rtbBaseName : GSM.ResolveNearestAirfieldName(st.position);
                    string a = !string.IsNullOrEmpty(st.manifestAssetType) ? st.manifestAssetType : (st.isTanker && !string.IsNullOrEmpty(st.tankerModelType) ? st.tankerModelType : "PARKED_ASSET");
                    var go = NewAircraftObject(st.id, b, a);
                    var m = go.AddComponent<OrbitMover>();
                    m.ImportState(st);
                    m.SetSymbolVisible(visible: false);
                    return true;
                }
                case Kind.Tanker:
                {
                    var st = JsonUtility.FromJson<TankerTrackMover.SavedState>(json);
                    string b = st.redeployFerryActive && !string.IsNullOrWhiteSpace(st.ferryDestinationBaseName) ? st.ferryDestinationBaseName.Trim()
                        : !string.IsNullOrWhiteSpace(st.rtbBaseName) ? st.rtbBaseName.Trim()
                        : !string.IsNullOrWhiteSpace(st.launchBaseName) ? st.launchBaseName.Trim()
                        : GSM.ResolveNearestAirfieldName(st.basePosition != Vector3.zero ? st.basePosition : st.position);
                    string model = string.IsNullOrEmpty(st.modelId) ? "TRISTAR_K1" : st.modelId;
                    var go = NewAircraftObject(st.id, b, model.Replace("_", " "));
                    go.AddComponent<TankerTrackMover>().ImportState(st);
                    return true;
                }
            }
            return false;
        }

        // Mirrors the in-flight missile branch of GameSaveManager.LoadFromPath.
        private static bool RestoreMissile(GSM.InFlightMissileState m)
        {
            if (m == null) return false;
            if (m.missileType == "AAM")
            {
                var go = new GameObject(m.missileId ?? "AAM_RESTORED");
                go.transform.position = m.position;
                var mover = go.AddComponent<AirToAirMissileMover>();
                var type = FriendlyMissileType.Generic;
                if (!string.IsNullOrEmpty(m.aamSubtype) && Enum.TryParse(m.aamSubtype, out FriendlyMissileType t)) type = t;
                mover.Configure(m.missileId ?? "AAM_RESTORED", m.startPosition, m.targetPosition, m.speedKnots,
                    m.maxRangeNm > 0f ? m.maxRangeNm : 100f, m.targetContactId, m.shooterContactId, m.isEnemyMissile,
                    m.hitProbability > 0f ? m.hitProbability : 1f, type);
                mover.transform.position = m.position;
                if (m.hasEnemyMarker && mover.GetComponent<EnemyMarker>() == null) go.AddComponent<EnemyMarker>();
                mover.RestoreTerminationState(m.terminationNotified, m.hitRegistered, m.terminationReason ?? "", m.prevTargetPosition, m.hasPrevTargetPos);
                return true;
            }
            if (m.missileType == "AGM")
            {
                var go = new GameObject(m.missileId ?? "AGM_RESTORED");
                go.transform.position = m.position;
                var mover = go.AddComponent<AirToGroundMissileMover>();
                mover.Configure(m.missileId ?? "AGM_RESTORED", m.startPosition, m.targetPosition, m.speedKnots,
                    m.blastRadiusMiles > 0f ? m.blastRadiusMiles : 2f, m.maxRangeNm > 0f ? m.maxRangeNm : 300f);
                mover.transform.position = m.position;
                if (!string.IsNullOrEmpty(m.targetContactId)) mover.SetTargetContact(m.targetContactId);
                if (!string.IsNullOrEmpty(m.shooterContactId)) mover.SetShooter(m.shooterContactId);
                if (m.hasEnemyMarker && mover.GetComponent<EnemyMarker>() == null) go.AddComponent<EnemyMarker>();
                if (m.hasNuclearBands)
                {
                    mover.SetNuclearDamageBands(new NuclearDamageBands
                    {
                        FireballRadiusMiles = m.nuclearFireballMiles,
                        ModerateBlastRadiusMiles = m.nuclearModerateBlastMiles,
                        ThermalRadiusMiles = m.nuclearThermalMiles,
                        LightBlastRadiusMiles = m.nuclearLightBlastMiles
                    });
                }
                mover.RestoreTuning(m.proximityMiles, m.homingGain, m.pk);
                return true;
            }
            if (m.missileType == "Trident")
            {
                var go = new GameObject(m.missileId ?? "TRIDENT_RESTORED");
                go.transform.position = m.position;
                var mover = go.AddComponent<TridentMissileMover>();
                mover.Configure(m.missileId ?? "TRIDENT_RESTORED", m.submarineId ?? "SUB_UNKNOWN", "HMS Unknown", m.startPosition, m.targetPosition, m.targetName ?? "Unknown Target");
                if (m.hasEnemyMarker && mover.GetComponent<EnemyMarker>() == null) go.AddComponent<EnemyMarker>();
                mover.RestoreInFlight(m.position, m.launchTimelineTime, m.tridentPhase);
                return true;
            }
            return false;
        }
    }
}
