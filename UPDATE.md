# Game update guide

What to do when Air Defender updates, and a list of every piece of game code the co-op mod depends on: where it is, how to find it again in a new build, and what happens when it changes.

The mod reaches the game in four ways. Only the first one is caught by the compiler:

| How the mod reaches the game | What happens when the game changes | How you find out |
|---|---|---|
| **Compile-time.** Direct calls into the publicized `lib/AirDefender.dll`, `typeof`, `nameof`. | The build fails. | `scripts/check-game-update.ps1` compiles against the new game. |
| **Harmony / AccessTools by name.** A missing target, a new overload or a renamed parameter. | Usually the log shows `Patching failed - co-op disabled`. Sometimes a different overload gets patched without any message. | `named-targets` section of the report, and the log markers. |
| **Plain strings and naming conventions** (SimulationGate type lists, GlobalTargets allowlists, save markers, satellite type names, scoring method prefixes). | **Silent.** That piece of the mod just stops working. | `string-refs` and `scoring-guards` sections, and the startup counts. |
| **Behaviour and copied code** (SpawnCatalog copies of GameSaveManager restore code, the radar-hit call path, reflection field sync). | Silent desync or wrong behaviour. | `il-hash`, `callers`, `field-schemas`, `sim-candidates` sections, and the `[DIGEST]` lines in a local test. |

---

## 1. Baseline (the build the mod works on)

Recorded by `scripts/snapshot-game-baseline.ps1` in `_baseline/26140834/` (git-ignored; it holds a copy of the game's `Managed` folder, the file hashes, `env.json` and `refdump.txt`). The check uses the newest baseline. The previous one, `_baseline/25378621/`, is kept for reference.

| Item | Value |
|---|---|
| Steam build id | **26140834** (depot 3985031, manifest 9057839742714295892); previous 25378621 |
| `AirDefender.dll` | 4,327,424 bytes, SHA256 `D24AB2EEC72BE8006EC7086F17D89EE45B38502312DE350644537DC7EB8A9338` |
| Handshake fingerprint (first 12 hex digits of the `AirDefender.dll` MVID) | `00fa7a643121` |
| Unity | 6000.4.0f1 (8cf496087c8f), **Mono** (`MonoBleedingEdge`; no `GameAssembly.dll`) |
| `boot.config` build-guid | `30ffacce65b74eef9bbfd6dfeb63c2df` |
| BepInEx / Doorstop / HarmonyX | 5.4.23.5 / 4.5.0 / 2.9.0 |
| Steamworks.NET | 2025.162.1 (SDK 1.62, `steam_api64.dll` 09.60.44.10) |
| Save schema | `CurrentNamedSaveSchemaVersion = 3`, `CurrentParkedAssetStateSchemaVersion = 1` |
| Startup log counts | `SimulationGate: 69 frame methods gated, 9 tick owners`, `Scoring guards: 29`, `Command routes: 86` (mod 0.2.2) |
| Mod | v0.2.1, `Protocol.Version` 3 |
| Decompile | `_decomp/AirDefender-26140834/` (ilspycmd 11.1), recorded in the baseline's `env.json`. **Keep it**: the next check diffs against it. `_decomp/AirDefender/` is the 25378621 decompile. |

---

## 2. Tools

| Tool | What it does |
|---|---|
| `scripts/snapshot-game-baseline.ps1 [-GameDir <dir>] [-Force]` | Records the current build into `_baseline/<buildid>/`. Run it once now, and again after the mod works on a new build. |
| `scripts/check-game-update.ps1 [-NewGameDir <dir>] [-Full] [-SkipDecompile]` | Compares the new build (default: the Steam install `..\..\Air Defender`) with the newest baseline. Writes `_update/<buildid>/REPORT.md`. |
| `scripts/check-game-update.ps1 -Logs [-LogDir <BepInEx dir>]` | After a test run, checks the startup counts against what they should be, and greps every failure marker. |
| `tools/RefDump` | `dump` writes a sorted text file of everything the mod depends on in a game DLL. `diff` compares two dumps. The check script runs both. |
| `scripts/build.ps1 [-GameDir] [-OutLib] [-OutDir]` | Same as before, but it now decides whether to re-publicize by **hash**, not timestamp (copied game files keep their old timestamps). The extra parameters build against another game folder without touching `lib/` or `bin/`. |

What `check-game-update.ps1` writes to `_update/<id>/`:

| File | Contents |
|---|---|
| `REPORT.md` | Verdict, environment comparison, findings by severity, compile errors, reference-diff summary, and the changed decompiled files the mod depends on. |
| `refdiff.md` | Per-section `-`/`+` lines from RefDump (full detail). |
| `refdump-old.txt`, `refdump-new.txt` | The raw dumps. |
| `build.log` | Full compiler output from building against the new game. |
| `manifest-diff.txt` | Every game file that was added, removed or changed. |
| `decomp-diff-stat.txt`, `decomp-diff.patch` | `git diff --no-index` of the old and new decompiles. The new decompile goes to `_decomp/AirDefender-<id>/`. |

Verdicts:

| Verdict | Meaning |
|---|---|
| `NO CHANGE` | Identical build. |
| `REBUILD ONLY` | Game code changed, but nothing the mod uses. |
| `REVIEW NEEDED` | The mod still builds, but referenced code, copied code or simulation types changed. |
| `CODE FIXES NEEDED` | Compile errors or missing references. |
| `BLOCKER` | The game is no longer Mono, or a similarly large change. |

---

## 3. Update-day procedure

The Steam install (`steamapps\common\Air Defender`) updates. **`Air Defender Coop` stays on the old build** until the mod is fixed.

1. **Before the update (once): already done for 26140834.** `pwsh scripts/snapshot-game-baseline.ps1`.
2. **Don't play co-op from the Steam install yet.** It also has BepInEx and the plugin installed. After the update, that plugin loads against the new game: patches may fail (co-op is switched off, the game still runs), or string lookups may silently break. To play the game unmodded in the meantime, rename `Air Defender\BepInEx\plugins\AirDefenderCoop`, or delete `winhttp.dll` there.
3. **Run the check:**
   ```
   pwsh scripts/check-game-update.ps1
   ```
   Open `_update/<newbuild>/REPORT.md`.
4. **Fix things in this order:**
   1. BLOCKER and engine findings ([section 6](#6-environment)).
   2. Compile errors from `REPORT.md` / `build.log`. Find the new name in `_decomp/AirDefender-<id>/`.
   3. Alerts (`MISSING`, `NOT FOUND`, `!!`) and the prio-1 sections `named-targets` and `string-refs`.
   4. Prio-2 sections: `il-hash` (re-read the changed game methods and update the mod's copy), `save`, `scoring-guards`.
   5. Prio-3 sections: new `NOT gated` sim-candidates, new `DROP` lines in field-schemas, `callers`.

   [Section 4](#4-reading-the-report-section--what-to-change) maps each section to the code to change.
5. **Move the coop copy to the new build.** Steam, BepInEx and mod files are left alone:
   ```
   robocopy "D:\SteamLibrary\steamapps\common\Air Defender" "D:\SteamLibrary\steamapps\common\Air Defender Coop" /E /XD BepInEx CoopMod .claude /XF winhttp.dll doorstop_config.ini .doorstop_version steam_appid.txt changelog.txt
   ```
   robocopy `/E` doesn't delete files the update removed. Check `manifest-diff.txt` for `-` lines and delete those by hand.
6. **Build and test:**
   ```
   pwsh scripts/build.ps1
   pwsh scripts/run-local-test.ps1 -HostScript host5x -ClientScript client-cmds -Seconds 120
   pwsh scripts/check-game-update.ps1 -Logs
   pwsh scripts/run-local-test.ps1 -HostScript host5x -ClientScript client-cmds -Clients 3 -JoinStagger 25 -Seconds 200
   pwsh scripts/check-game-update.ps1 -Logs
   ```
   `build.ps1` re-publicizes `lib/AirDefender.dll` automatically, because the hash changed.
7. **Versions:**
   - Bump `Plugin.Version` (`src/AirDefender.Coop/Plugin.cs`) and `<Version>` (`AirDefender.Coop.csproj`) together.
   - Bump `Protocol.Version` (`Net/Protocol.cs`) if any message layout, `MsgType`, `RouteList` order or `SpawnCatalog.Kind` changed.
   - The game fingerprint updates by itself when `AirDefender.dll` changes.
8. **Release:**
   - Run `pwsh scripts/package.ps1`.
   - Update the README status and limitations if behaviour changed.
   - If a new BepInEx was needed, update `.deps/`, `package.ps1`, `Install-AirDefenderCoop.ps1` and the README.
9. **New baseline:** run `pwsh scripts/snapshot-game-baseline.ps1`, update [section 1](#1-baseline-the-build-the-mod-works-on), then delete old `_update/` and `_decomp/AirDefender-*` folders you no longer need.
10. Reinstall the plugin into the Steam install with `pwsh scripts/install.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Air Defender"`, and ask the other players to update both the game and the mod.

---

## 4. Reading the report: section → what to change

| RefDump section | A change there means | Change in the mod |
|---|---|---|
| `compile-refs` | Every game type, method and field the built mod DLL uses, with the mod types that use it (`<-`). `MISSING` means the build will break. | Use the file named after `<-`. Find the new name with `rg -n "<oldName>" _decomp/AirDefender-<id>`. |
| `named-targets` | Members the mod looks up **by name** at runtime: Harmony attribute targets, `AccessTools.Method`, `RouteList`. Watch for `overloads=` going up (an ambiguous match), and for parameter names changing (Harmony injects arguments by name). | Pin the overload with an explicit `Type[]`, or rename the patch parameter. See [5.2](#52-harmony-patches-patchescs), [5.4](#54-command-routes-commandsroutelistcs-methodroutercs). |
| `string-refs` | SimulationGate types, GlobalTargets allowlist fields, parked-asset markers, satellite type names. `NOT FOUND` / `MISSING` fail silently. The `SimulationGate TOTAL` line is the count the mod should log. | [5.1](#51-simulationgate-puppetsimulationgatecs), [5.6](#56-global-state-replicationglobaltargetscs-customglobalscs), [5.7](#57-entity-spawns-replicationspawncatalogcs-entityreplicatorcs). |
| `scoring-guards` | Public static void `TriStateScoringService` methods named `On*`, `Add*` or `Apply*`. These are made host-only. | A renamed scoring method may drop out of the guard. Check `ReplicationPatches.ApplyScoringGuards`. |
| `save` | Save schema constants and the field lists of every save record the mod reads. | `WorldSync.cs`, `SpawnCatalog.cs`. |
| `il-hash` | The body of a game method the mod copies or relies on has changed. | Diff that method in the decompiles and update the mod's copy or assumption. The text in brackets on each line says which part of the mod. |
| `sim-candidates` | Every MonoBehaviour with `Update`/`LateUpdate`/`FixedUpdate` or a `TickManager.Register` call, marked `gated` or `NOT gated`. | A **new** `NOT gated` type that simulates (spawns, moves, decides, scores) must be added to a SimulationGate list. If it only presents (UI, audio, rendering), leave it. |
| `field-schemas` | What reflection sync sends or drops for each global target and each contact component type. | A new `DROP ... (unsupported type)` on real game state means it won't sync. Extend `FieldCodec` or add a `CustomGlobals` entry. |
| `callers` | Callers of `TrackManager.NotifyHit`, and every file, Steam Cloud or PlayerPrefs writer. | A new NotifyHit caller is forwarded host to client (fine). A new ground-radar path outside `RadarPrimaryHistory.Update` breaks `RadarHitSync`. A new writer may escape `--coop-sandbox`. |
| `monobehaviours` | All MonoBehaviour types (StateReplicator's type ids are their sorted index). IContact types also show their `contactId` field. | Ids adjust automatically. `contactId-field=MISSING` on a contact type breaks `SpawnCatalog`'s clone path for that type. Today `RadarCircleMover` already has no `contactId` field. |
| `surface` | Full member list of every game type the mod touches. | Background for the sections above. |
| `asm` | The fingerprint. It always changes when the code changes. | Nothing to do. |

---

## 5. Reference inventory

Paths:
- `D` = the decompile folder: `_decomp/AirDefender/AirDefender/` for the old build, `_decomp/AirDefender-<id>/AirDefender/` for the new one.
- Game types live in `D/<Type>.cs`, except `D/../AirDefender.Failures/FailureManager.cs` and `D/../AirDefender.SaveLoad.Storage/LocalFileStorage.cs`.
- Mod paths are under `src/AirDefender.Coop/`.

Each line below shows the old line number in the decompile. To find a reference in a new build, search for its name; line numbers will have moved:
```
rg -n "<name>" _decomp/AirDefender-<id>/AirDefender/<Type>.cs
```

### 5.0 How patches fail (`Plugin.cs`)

`Plugin.Awake` runs `Harmony.PatchAll`, then `SandboxPatches.Apply`, `SimulationGate.Apply`, `ReplicationPatches.ApplyScoringGuards`, `RouteList.Apply` and the remaining `Apply`s, all inside one `try`. Any exception logs **`Patching failed - co-op disabled: <exception>`** and turns co-op off. Patches applied before the failure stay applied.

| How the target is looked up | Not found | New overload appears |
|---|---|---|
| `[HarmonyPatch(type, name)]` with no argument types | Exception → co-op disabled | `Ambiguous match` → co-op disabled |
| `AccessTools.Method(type, name)` | Returns null and HarmonyX warns `AccessTools.Method: Could not find...`. `h.Patch(null)` then throws → co-op disabled | If one of the overloads takes no parameters, **that one is chosen silently**. Otherwise `AmbiguousMatchException` → co-op disabled |
| `AccessTools.Method(type, name, Type[])` | Null + warning | Not affected |
| A patch parameter bound by name (`contactId`, `target`, ...) that the game renamed | Harmony throws while patching → co-op disabled | — |

### 5.1 SimulationGate (`Puppet/SimulationGate.cs`)

**How it works.** Each name is resolved as `"AirDefender." + name` against the game assembly. For each type, the mod patches `Update`, `LateUpdate` and `FixedUpdate` if the type **declares** them (inherited ones are ignored) with a prefix that skips them on clients. Only the exact type is gated, not subclasses.

**How it breaks.**
- A missing type logs `SimulationGate: type X not found` and that type keeps simulating on clients, which desyncs them.
- A type that loses its `Update` produces **no log**; only the total count drops.

**Expected count:** `SimulationGate: 69 frame methods gated, 9 tick owners`. The 68 types declare 69 frame methods, because `EnemyFighterEscortMover` declares both `Update` and `LateUpdate`, and `NimrodEwEmitter` and `NuclearDetonationWatcher` declare only `LateUpdate`.

**Host simulation depth.** On the host the same prefix (with a finalizer, sharing `__state`) counts `SimulationGate.HostSimDepth` while a gated frame method or tick runs. `PresentationSync` forwards sounds and alert banners produced inside that window ([5.9](#59-tracks-radar-time-and-presentation)). Coroutines a gated method starts are not counted.

**EntityLogicTypes (43):**
- AircraftAltitudeManager, AirToAirMissileMover, AirToGroundMissileMover, AirwayFollowerMover
- AwacsMover, BakedRouteFollowerMover, BallisticLaunchMover, CAPPairController
- CivilianTrafficController, DistanceTravelMissileLauncher, EnemyAircraftController, EnemyAltitudeController
- EnemyFighterAI, EnemyFighterEscortMover, EnemyMovementController, EnemyTankerMover, EnemyWeaponController
- IfrApproachController, IfrDepartureSpeedController, InterceptorOnboardRadar, JammingCapability, LegSpeedController
- MirvWarheadMover, NimrodSigintMover, OrbitMover, QRAEscortMover, QRAInterceptorMover, RadarCircleMover
- RigHelicopterShuttleController, SeadAircraftMover, TakeoffTurnMover, TankerSupportMover, TankerTrackMover
- TridentMissileMover, VfrCrossCountryController, VfrLocalSortieController, WaypointPathMover
- ForcePendingTag, MissionTargetController, NimrodEwEmitter, LandingAircraftMarker
- ReindeerAttackController, SantaTrackDatalinkFeed

**WorldSimTypes (25):**
- AirfieldArrivalSequencer, AirfieldDepartureSequencer, ArmageddonGameEndService, BloodhoundSamService
- Bootstrapper, ChinookSupplyService, CriticalBallisticLaunchDirector, CriticalRaidOrchestrator
- EnemyCampaignDirector, EnemyIntelligenceService, EnemyMissionScheduler, F3Spawner
- Failures.FailureManager, Fr24LiveTrafficService, HerculesSupplyService, HistoricalLaunchScheduler
- IdoDayOneManager, IdoIfrTrafficSpawner, IdoVfrTrafficSpawner, RadarDeceptiveJammingInjector
- RigHelicopterShuttleSpawner, ScenarioDirector, ScenarioRunner
- TrgSortieController (added for 26140834: one per air combat training sortie; its `Update` steps the engagement)
- NuclearDetonationWatcher (mod 0.2.2: a scene object whose `LateUpdate` destroys aircraft and adds casualties when an enemy reaches the map centre)

**Not gated on purpose.** `RedArrowsDisplayService` (26140834) flies a timetabled display from the shared clock, so it runs on every peer and its jets are local authority ([5.7](#57-entity-spawns-replicationspawncatalogcs-entityreplicatorcs)). The other new `NOT gated` types in 26140834 only present: `AdTrainingAreaOverlayRenderer`, `GameplayMusicCues`, `IdentBatchConfirmUI`, `OtherChannelsBackgroundPlayer`, `TopRightMenuUI`.

**TickSimTypes (9).** These have their `TickManager` callbacks wrapped instead of their frame methods:
- RadarCoverageDespawn, ManifestEnforcer, IfrSeparationMonitor, TensionStateWatcher
- SantaEventDirector, DatalinkL11Manager, ForceCallsignSetter, CivilianLandingEscortService
- GlobalInterceptorMonitor

**`TickManager.Register`.** Patched with `AccessTools.Method(typeof(TickManager), nameof(Register))`, without argument types. The prefix binds the game parameters **by name**, `target` and `callback`.
- Old signature: `public static void Register(MonoBehaviour target, Action callback, string name, float intervalSeconds, bool useUnscaled = false)` at `TickManager.cs:174`.
- Each tick type calls `TickManager.Register(this, ManagedTick, "<Type>.ManagedTick", ...)`. Find them with:
  ```
  rg -n "TickManager.Register\(" D
  ```

**After an update:**
- Review `sim-candidates` for new `NOT gated` simulation types.
- Recent content types that are already gated include the Santa event, Fr24 live traffic and the IDO day-one spawners.

### 5.2 Harmony patches (`Patches/*.cs`)

None of these attribute patches specify argument types, so **a new overload of any of them disables co-op**. The parameter names in the last column are bound by name.

| Mod file | Target (old signature, decompile line) | Patch | Parameters bound by name |
|---|---|---|---|
| ReplicationPatches | `ContactRegistry.Register(IContact contact)` :18 | postfix | `contact` |
| ReplicationPatches | `TimeControlsUI.SetTimeScale(float scale)` (private) :383 | prefix | `scale` |
| ReplicationPatches | `TimeControlsUI.HandleFocusChanged(bool hasFocus, bool inEditor, bool inGame)` (internal, 26140834) | prefix: skip the background pause while co-op is on | `hasFocus` |
| ReplicationPatches | `TrackManager.TrySetOperatorClassification(string contactId, string identifier, Color color)` → `ClassificationApplyResult` :2295 | prefix | `contactId, identifier, color, __result` |
| ReplicationPatches | `IdentificationScoringEvaluator.Evaluate(TrackRenderer track, string contactId, int serialId, string oldIdentifier, string newIdentifier, bool triggerQraOnThreatIdent)` :9 | prefix | all six names and `__result`. On a client it opens the QRA dialog itself (`IsNewThreatIdent`, `OpenQraLaunchOptionsFor`, `ResolveCanonicalContactId`); the host's `HostIdentify` passes `triggerQraOnThreatIdent: false` |
| ReplicationPatches | `TrackManager.PropagateIdentifierAcrossWave(string, string, Color)` :2341 | prefix | — |
| LifecyclePatches | `GameResetService.IsProcessLifetimePlatformRoot(GameObject root)` (private static) :298 | postfix | `root, __result` |
| LifecyclePatches | `GameResetService.ResetToMainMenu()` :43 | prefix | — |
| LifecyclePatches | `GameSaveManager.LoadFromPath(string path, string sessionName)` (private static) :1803 | prefix + postfix | `__result` |
| LifecyclePatches | `GameSaveManager.SaveAs(string sessionName)` :1431 | prefix | `__result` |
| LifecyclePatches | `Steamworks.SteamAPI.RestartAppIfNecessary(AppId_t)` (Steamworks.NET) | prefix | `__result` |
| SandboxPatches | `UnityEngine.PlayerPrefs` Set/Get/HasKey/DeleteKey/DeleteAll/Save (explicit `Type[]`) | prefix | `key, value, defaultValue, __result` |
| SandboxPatches | `LocalFileStorage.WriteBytesAtomicSync(string fullPath, byte[] data)` :75 and `WriteTextAtomicSync(string fullPath, string text)` :95 | prefix skip | — (no argument types; a new overload is ambiguous) |
| SandboxPatches | `Steamworks.SteamRemoteStorage.FileWrite`, `FileDelete` | prefix → false | `__result` |
| RadarHitSync | `RadarPrimaryHistory."Update"` (**string**, private) :311 | prefix + finalizer | — |
| RadarHitSync | `TrackManager.NotifyHit(string contactId, Vector3 worldPosition, float sweepPeriodSeconds, int stationId=0, int sweepSequence=0, bool isL11=false)` :1191 | prefix | all six names |
| PresentationSync | `AsmaServiceImpl.StartPostNow(AsmaMessage m)` (private) :463 | prefix + postfix | **`m`** |
| PresentationSync | `VoiceAudioQueue.EvaluateShouldPlay(VoiceAudioRequest request)` (private static) :486 | postfix | `request, __result` |
| PresentationSync | `SfxServiceImpl.PlayAlertNonVoice(AudioClip clip, float baseVolume=1, Func<bool> shouldPlay=null)` (private) :985 | prefix | `clip, baseVolume` |
| PresentationSync | `UnityEngine.Resources.Load(string, Type)` | postfix | `path, __result` |
| PresentationSync | `SfxServiceImpl.PlayInternal(AudioClip clip, float volume)` and `PlayInternalNormalized(AudioClip clip, AudioCategory category, float baseVolume)` (private, explicit `Type[]`) | prefix | `clip, volume` / `clip, category, baseVolume` |
| PresentationSync | `NuclearBlastEffect.Spawn(Vector3 worldPos, float radiusMiles, NuclearDamageBands? damageBands, float bloomMiles, float dissipateMpm)` (explicit `Type[]`) | postfix | all five names |
| PresentationSync | `AlertOverlay.Show(string message, float seconds)` and `(…, Color color)`; `SessionBannerUI.ShowHudNotification` with the same two shapes (explicit `Type[]`) | prefix | `message, seconds, color` |
| PresentationSync | `GameplayMusicCues.NotifyFriendlyShotDown()`, `NotifyEasPlayed(float easClipSeconds)` | postfix | `easClipSeconds` |
| EndGameSync | `ArmageddonGameEndService.ShowEndGameUI(bool victory)` (private) | postfix | `victory` |
| EndGameSync | `ArmageddonEndGameUI.DisplaySantaDefeat()`; private `ContinuePlaying()` (prefix + postfix), `ReturnToMenu()`, `TryAgain()`, `NavigateToTradeTraining()` | postfix / prefix | — |
| ReplicationPatches | `RadioChannelUI.PruneStaleChannels()` (private) | prefix: skipped on clients | — |
| SimulationGate | `TickManager.Register` (see [5.1](#51-simulationgate-puppetsimulationgatecs)) | prefix | `target, callback` |
| SimulationGate | every gated frame method | prefix + finalizer | `__state` only |

**Other compile-time uses in `ReplicationPatches`:**
- `ClassificationApplyResult.Applied/Success/Message`
- `TrackManager.Instance/GetRendererForContact`
- `TrackRenderer.GetIdentifier/GetTrackSerialId`
- `AsmaService.PostSitrep(..., AsmaMessageSeverity.Advisory)`
- `ForcePendingTag.ClearPendingRequirement`

### 5.3 Scoring guards (`Patches/ReplicationPatches.cs` `ApplyScoringGuards`)

`TriStateScoringService`'s public static void methods whose names start with `On`, `Add` or `Apply` are skipped on clients. The convention is checked at runtime against the game's **real** visibility, not the publicized DLL. Expected log line: **`Scoring guards: 29`**.

The 29 methods (`OnTrainingSortieCredited` is new in 26140834):
- OnTrainingSortieCredited
- AddPs, AddPp, ApplyFailureImpact
- OnCivilianAircraftShotDown_AfterMisIdentification, OnWeaponsHotAuthorised
- OnCorrectIdentification, OnIncorrectIdentification, ApplyDeferredMisidentificationPenalty
- OnIncorrectIdentification_CivilAsEnemy, OnIncorrectIdentification_EnemyAsAirliner
- OnEnemyAircraftShotDown, OnEnemyMissileShotDown, OnEnemyTargetShotDown, OnCivilianAircraftShotDown
- OnCityDestroyed, OnEnemyFeatureDestroyed, OnCityDestroyedByNuke, OnEnemyReachedFeatureUnintercepted
- OnEnemyCityOverflightMinute, OnEnemyRadarOverflightMinute, OnEnemyAirfieldOverflightMinute, OnEnemyStrategicOverflightMinute
- OnEnemyUnnoticedInsideFir, OnSpooferUnidentified, OnZombieIntercepted
- OnCorrectIdentificationProcedure, OnIncorrectIdentificationProcedure, OnMissileFiredInsideFir

Not guarded on purpose: `SetScores`, `Reset*`, `Restore*`, `SetImplementation`. A **new** scoring method with another prefix (for example `Record*`) is not guarded. Look for it in `scoring-guards` and `surface`.

### 5.4 Command routes (`Commands/RouteList.cs`, `MethodRouter.cs`)

**How routes are identified.** **The order of `RouteList` entries is the wire id. Only ever append.** Removing or reordering entries means bumping `Protocol.Version`.

**How methods are looked up.** Names use `nameof`, so a rename is a compile error.
- Entries with no argument types use `AccessTools.Method(type, name)`. A new overload is ambiguous and disables co-op, or silently picks the zero-parameter overload.
- ★ marks entries pinned with an explicit `Type[]`. If one of those methods changes its parameter types, the log shows `Route: T.M not found` and that route does nothing (the slot stays, so the count stays 86).
- A parameter that `ArgCodec` can't encode logs `Route: T.M has unsupported parameter p:T`.

**Blocking.** A route blocks (waits one round trip) when the method has a non-void return or a by-ref parameter. Ids 19 and 46 are forced.

**Instance targets.** Instance methods find their target by contact id. For UI and selector components (`AssetsWindowUI`, `AirfieldClickHandler`, `AwacsTargetSelector`, `NimrodSigintSelector`, `TankerLaunchSelector`, `RadarStation`), they use the **scene GameObject name** with `GameObject.Find`. Renamed scene objects break this silently; the decompile won't show it.

| Id | Method | Parameters | B |
|---|---|---|---|
| 0–1 | `QRAQuickLaunchService.` LaunchForTrack, LaunchForContact: **reserved** (`MethodRouter.Reserve`) since 0.2.2. They only open the QRA launch-options dialog, which must open on the clicking player's screen. | | |
| 2–10 | `QRAQuickLaunchService.` LaunchWithOptions, LaunchForContactAuto, IssueTaskToLaunchGroup, IssueRefuelToLaunchGroup, IssueRtbToLaunchGroup, CascadeSpeedModeToLaunchGroup, CancelPendingLaunch, WireMintedSortieForAirborneCapEngage, TryRestoreRecoveredPairForTasking | (single overloads) | 4,5,8,9,10 |
| 11–12 | `ManualQRACommander.` LaunchInterceptors, LaunchTankerCap | | 11 |
| 13–16 | `QRACommander.` SetAutoQRAEnabled, LaunchTankerCap, MassScrambleAllBases, MassScrambleAllBasesAgainstTarget | | |
| 17–22 | `Bootstrapper.` SpawnInterceptorAtBase, SpawnInterceptorAtBaseUnarmedOk, SpawnMilitaryAsset (forced blocking), TryRelocateStationedInterceptor, TryRelocateStationedInterceptorPair, TryRelocateSupportAsset | | all |
| 23 | `AssetsWindowUI.TryLaunchInterceptorPair` | string, string, string, out QRAInterceptorMover | B |
| 24–25 | `AirfieldClickHandler.` TryLaunchInterceptorPairForEngage, TryLaunchInterceptorPairForIdentify | string, string, out QRAInterceptorMover | B |
| 26 | `AwacsTargetSelector.SpawnAwacsForTask` | string, string → AwacsMover | B |
| 27 | `NimrodSigintSelector.SpawnNimrodForTask` | string, string → NimrodSigintMover | B |
| 28 | `TankerLaunchSelector.LaunchTanker` | Vector3 | |
| 29 | `CAPPairService.LaunchCAPPair` ★ | string, Vector3, string | B |
| 30 | `CAPPairService.LaunchCAPPair` ★ | string, string, Vector3, string | B |
| 31 | `CAPPairService.TryAssignExistingCapGroup` | QRAInterceptorMover, string, Vector3 | B |
| 32 | `TankerSupportService.EnsureSupportFor` | QRAInterceptorMover | |
| 33 | `AlternateRecoveryService.TryCommand` | MonoBehaviour, string, out string | B |
| 34 | `HerculesSupplyService.TryDispatchCustom` ★ | string, string, int, int, int, out string | B |
| 35 | `HerculesSupplyService.TryDispatchCustom` ★ | string, string, string, int, int, int, out string | B |
| 36 | `HerculesSupplyService.TryRedeploy` | string, string, out string | B |
| 37–38 | `ChinookSupplyService.` TryDispatch (string×3, int×3, out string), TryRedeploy (string, string, out string) | | B |
| 39 | `AwacsSelectionService.SetActiveAwacs` | string | |
| 40–45 | `AirspaceClosureService.` TriggerClosure, ClearClosureFlag, ForceClose, ForceOpen, ReopenAirspace, TryReopenAfterThreatClear | | 45 |
| 46 | `GroundAllAircraftService.GroundAllCivilianAircraft` | — → `GroundingStats` (can't be encoded; forced non-blocking) | |
| 47 | `BloodhoundSamService.EnableNetwork` | — | |
| 48 | `DatalinkL11Manager.SetEnabled` | bool | |
| 49 | `RadarStation.SetRadarPowered` | bool (instance found by GameObject name) | |
| 50–53 | `QRAInterceptorMover.` RetaskTarget(string), AssignIdentTask, AssignEngageTask, AssignEscortTask (string, bool) | | |
| 54 | `QRAInterceptorMover.RequestRefuel` ★ | () | |
| 55 | `QRAInterceptorMover.RequestRefuel` ★ | Transform | |
| 56–58 | `QRAInterceptorMover.` RequestStatusUpdate, SetSpeedMode(SpeedMode), SetMission(MissionType) | | |
| 59 | `QRAInterceptorMover.SetTakeoffSlot` ★ | float | |
| 60 | `QRAInterceptorMover.SetTakeoffSlot` ★ | float, bool | |
| 61–62 | `QRAInterceptorMover.` SetFormationSlot(int, int, float), SetPairAsLeader(QRAInterceptorMover) | | |
| 63–66 | `AwacsMover.` RequestRefuel, SetRadarEnabled(bool), SetLoiterAt(Vector3), ReturnToBase(Vector3, string, bool) | | |
| 67–69 | `NimrodSigintMover.` SetLoiterAt, RequestRefuel, ReturnToBase(Vector3, string, bool) | | |
| 70–71 | `OrbitMover.` SetAsTanker(bool), SetTankerModelType(string) | | |
| 72 | `OrbitMover.ReturnToBase` ★ | Vector3, bool | |
| 73 | `OrbitMover.ReturnToBase` ★ | Vector3, string, bool | |
| 74–80 | `TankerTrackMover.` EnableDelayedTakeoff, SetAirborneAltitude, RetaskToNewTrack (→ bool), SetTrackMetadata, RequestRefuelFromPlayer (→ bool), SetRacetrackWidthNm, SetLaunchBaseName | | 76, 78 |
| 81 | `TankerTrackMover.ReturnToBase` ★ | Vector3, string, bool | |
| 82 | `TankerTrackMover.ReturnToBase` ★ | Vector3, bool | |
| 83 | `AircraftAltitudeManager.SetTargetAltitude` | float | |
| 84 | `TrgSortieService.TryStart` (26140834) | QRAInterceptorMover, QRAInterceptorMover, Vector2, out TrgSortieController, out string | B |
| 85 | `TrgSortieService.TryLaunchFromBase` (26140834) | string, string, Vector2, int, out TrgSortieController, out string | B |

The TRG routes are called from `AssetMissionTargetSelector.TryCommitTrg`. The out `TrgSortieController` comes back as its GameObject name and usually resolves to null on the client; the caller only uses the bool.

The nested enums `QRAInterceptorMover.MissionType` and `SpeedMode` are sent as numbers. If the game reorders them, it doesn't matter, because both sides run the same build.

### 5.5 Argument encoding (`Commands/ArgCodec.cs`)

**Types `ArgCodec` can encode:**
- scalars and any enum (sent as its number)
- Unity `Vector2/3/4`, `Quaternion`, `Color`, `Rect`, `Vector2Int`
- `List<string>`, `string[]`, `float[]`, `int[]`

**Game types it handles specially:**

| Game type | How it's sent | How it's resolved on the host |
|---|---|---|
| `IContact` (`.ContactId`) | contact id | `ContactRegistry.TryGetContact` |
| Any game `Component`, `MonoBehaviour`, `GameObject` or `Transform` | IContact id, otherwise the GameObject name | `ContactRegistry`, then a scan of MonoBehaviours, then `GameObject.Find` |
| `TrackRenderer` | `.ContactId` | `TrackManager.GetRendererForContact` |
| `MapFeaturesStorage.Feature` | fields `id, name, type (MapFeatureType as int), worldX, worldY, population, normX, normY` | `MapFeaturesStorage.Load()` by `id` |

A new parameter type on a routed method that isn't in this list logs `Route: ... unsupported parameter`.

### 5.6 Global state (`Replication/GlobalTargets.cs`, `CustomGlobals.cs`)

Global state is synced field by field (`StateReplicator` + `FieldCodec`). The `field-schemas` section shows exactly what is sent and what is dropped.

**FieldCodec rules:**
- **Fields considered:** every declared instance field, walking from the type's base class down to the type itself (static targets use only the type's own fields). Each class level is sorted by field name.
- **Skipped:**
  - `const` fields
  - static readonly fields, except `List`/`Dictionary`/`HashSet`
  - compiler-generated fields, except auto-property backing fields
  - delegates
  - names containing `cache Cache debug Debug scratch Scratch lastLog nextLog _tmp`, unless the type has an allowlist
  - unsupported types
- **Supported types:**
  - scalars, enums and the Unity structs
  - `Transform`/`GameObject`/game IContact MonoBehaviour, sent as a contact id
  - `T[]` and `List<T>` of a scalar
  - `Dictionary<string, scalar>`
  - `HashSet<string>`
- **Unsupported types are dropped with no log.**

**Targets (`GlobalTargets.Build`):**

| Key | Type | Instance / static |
|---|---|---|
| `Scoring` | `TriStateScoringServiceImpl`, via `TriStateScoringService._impl`. The mod assumes `_impl` is that concrete class. | instance |
| `AirfieldFuelManager` | | instance (`FindAnyObjectByType`) |
| `AirfieldWeaponsManager` | | instance + `.static` |
| `CasualtyService` | | instance |
| `EnemyCampaignDirector` | | instance |
| `CriticalRaidOrchestrator` | | instance + `.static` |
| `BloodhoundSamService` | | instance |
| `EnemyIntelligenceService` | | instance |
| `MapFeaturesRenderer` | | instance |
| `TrackManager` | | instance |
| `CampaignVictoryTracker` | | static |
| `DestroyedAirfieldService` | | static |
| `AssetInventory` | | static |
| `AirspaceClosureService` | | static |
| `QRACommander` | | static |
| `SessionStatisticsService` | | static |
| `RadarStation` | | static |
| `TrgProficiencyStore` | completed TRG engagements (auto-property backing field) | static |
| `Radar:<stationName>` | each entry of the private `RadarStation.registry`, keyed by `stationName` (names must be unique) | instance |

**Allowlists** (only these fields are sent for these types; each name is a string):

| Type | Fields |
|---|---|
| `RadarStation` | `radarPowered`, `cycleState`, `sweepSuppressed`, `lockedStationIds`, `fylingdalesUnlocked` |
| `MapFeaturesRenderer` | `destroyedFeatureIds` |
| `TrackManager` | `contactIdToSerial`, `nextSerialId`, `ewRevealedAircraftTypes`, `visuallyIdentifiedAircraftTypes`, `enemyInsideFirPenaltyApplied`, `raidWarnedThroughLadder`, `pendingUnknownByEw`, `pendingHostileByEw`, `pendingHostileByWeaponIntel`, `nextQraSuffixOctal` |

**CustomGlobals.** Sent as JSON through `JsonUtility` whenever they change. Entries marked *re-applied* are also written again on the client every 2 s from the last value received, because local game code writes them too:
- `VirtualDeltas` uses `Bootstrapper.ExportVirtualDeltas()`, `ClearVirtualDeltas()` and `ImportVirtualDeltas(list)`. `Bootstrapper.VirtualDeltaEntry` must stay `[Serializable]` with public fields `baseName, assetType, delta`.
- `ChaffClouds` (26140834) copies the host's active slots of `ChaffCloudRegistry`: the private static `s_clouds` (`Cloud[64]`, fields `active`, `painted`, `origin`, `bornGameSeconds`) and the private `Retire(int)`. Clouds are released only by gated code (`QRADefensiveService`, `EnemyChaffService` via `AirToAirMissileMover`), but every radar paints them in `RadarPrimaryHistory.PaintChaffClouds`. Their drift uses `WindService`, which is fixed at 240° (nothing calls its setters), and the shared timeline.
- `PendingQra` (0.2.2, re-applied, `PanelMirrors`) copies the private statics of `QRAQuickLaunchService` in place: `s_pendingOrProcessingTargets`, `s_targetsLaunched`, `s_cancelledTargets`, `s_pendingSorties` (`HashSet<string>`), `s_desiredLaunchCountByTarget`, `s_pendingSortieByTarget`, `s_launchSortieByInterceptorContact` (`Dictionary<string,…>`), and `s_pendingLaunchInfoByTarget` (struct `PendingLaunchInfo { launchAt, usesTimeline }`). An unscaled ETA is sent as the seconds remaining. Readers: `ActiveOperationsPanel` ("QRA Pending … T-m:ss"), `RadioChannelUI`, `IsLaunchPendingOrCommittedForTarget`.
- `HerculesFlights` / `ChinookFlights` (0.2.2) send `ExportSaveState()` and replace the client's `Instance._activeFlights` contents. They don't use `ImportSaveState`: that also purges flight contacts and calls `Bootstrapper.RegisterDynamicHolding` each time. Readers: `GetActiveFlights`, `GetFlightStatusForUi`, `TryGetOriginBaseForContactId`.
- `RadioChannels` (0.2.2, re-applied) mirrors `RadioChannelUI.instance`: `channels` (`RadioChannel` fields `channelNumber, sortieKey, displayLabel, leadCallsign, wingmanCallsign, leadContactId, wingContactId`), `channelBySortie`, `channelByContact`. Each player keeps their own `activeChannel` (`enabled` marks it). It uses private `EnsureExists`, `RemoveChannel`, `ActivateChannel` and `s_panelVisible`. This carries the TRG channel labels ("TRG <area>") and pending-sortie channels.

### 5.7 Entity spawns (`Replication/SpawnCatalog.cs`, `EntityReplicator.cs`)

**Capture (host).** `GameSaveManager.BuildSnapshot()` (private) gives the host's snapshot, read from these `SaveBlob` fields. The `Kind` byte is a wire contract: only append new values.

| Kind | Byte | SaveBlob field | Record type | Id field |
|---|---|---|---|---|
| Civil | 1 | `civilianAircraft` | `GameSaveManager.CivilianAircraftState` | `contactId` |
| Vfr | 2 | `vfrAircraft` | `GameSaveManager.VFRAircraftState` | `contactId` |
| Enemy | 3 | `activeEnemies` | `EnemyAircraftController.SavedState` (struct) | `contactId` |
| Interceptor | 4 | `activeInterceptors` | `GameSaveManager.ActiveInterceptorState` | `id` |
| Missile | 5 | `inFlightMissiles` | `GameSaveManager.InFlightMissileState` | `missileId` |
| CapAnchor | 6 | `capAnchors` | `GameSaveManager.CAPAnchorState` | `contactId` |
| Stationed | 7 | `stationedMilitary` | `GameSaveManager.MilitaryStationedAsset` | `id` |
| Awacs | 8 | `parkedAssets` (`ParkedAssetEntry`) with `baseName == "__AWACS_STATE__"` | `AwacsMover.SavedState` (JSON in `assetTypeName`) | `.id` |
| Nimrod | 9 | `parkedAssets`, `"__SIGINT_STATE__"` | `NimrodSigintMover.SavedState` | `.contactId` |
| Orbit | 10 | `parkedAssets`, `"__ORBIT_STATE__"` | `OrbitMover.SavedState` | `.id` |
| Tanker | 11 | `parkedAssets`, `"__TANKER_TRACK_STATE__"` | `TankerTrackMover.SavedState` | `.id` |
| Clone | 20 | (structural copy fallback) | | |

The four marker strings must match the game's `GameSaveManager.IsEmbeddedParkedAssetStateMarker`. The mod does **not** check `ParkedAssetEntry.stateSchemaVersion`.

**Restore (client).** For Civil, Vfr, Enemy and Interceptor, the mod calls the game's private statics with a one-element list:
- `RestoreCivilianAircraft`, `RestoreVFRAircraft`, `RestoreActiveEnemies`, `RestoreActiveInterceptors`
- Assumption: they replace only the matching id. If an update makes them clear every entity of that kind, every spawn would wipe the others. Check the `il-hash` lines.

**Copies of game code in the mod** (re-read the game method when its `il-hash` changes):

| Mod code | Copies | Members it uses |
|---|---|---|
| Missile | in-flight branch of `GameSaveManager.LoadFromPath` | `AirToAirMissileMover.Configure` (10 args) and `RestoreTerminationState`; `AirToGroundMissileMover.Configure` (6), `SetTargetContact`, `SetShooter`, `SetNuclearDamageBands`, `RestoreTuning`; `TridentMissileMover.Configure`, `RestoreInFlight`; type strings `"AAM"`, `"AGM"`, `"Trident"`; `Enum.TryParse` of `aamSubtype` |
| Stationed | one iteration of `GameSaveManager.RestoreStationedMilitary` | `ManifestTag.Configure`, `QRAInterceptorMover.SetAircraftTypeName`, `ConfigureWithId`, `SetWeaponsAndFuel` (both overloads); aircraft name → asset type map `F3`, `GR1`, `Jaguar→JAG`, `Harrier→HAR`, `Hawk→HAWK`, `Lightning→LIGHTNINGF6`, `Phantom→PHANTOMFGR2`, else `INT` |
| Awacs / Nimrod / Orbit / Tanker | `GameSaveManager.RestoreParkedAssets` | `new GameObject(id)`, then `ManifestTag.Configure`, then `<Mover>.ImportState(SavedState)`; `NimrodSigintMover.BuildCallsign`, `FixedCallsignTag.SetCallsign`, `OrbitMover.SetSymbolVisible`, `GameSaveManager.ResolveNearestAirfieldName` |
| CapAnchor | one iteration of `GameSaveManager.RestoreCAPAnchors` | `CapNumberRegistry.ClaimFromSave(contactId, capNumber)` (26140834: keeps the host's "CAP n" label), then `GameSaveManager.EnsureCapAnchorExists(string, Vector3)` |

**Clone path.**
- The host sends each component's `AssemblyQualifiedName`. The client resolves it with `Type.GetType` and sets the id through a field named **`"contactId"`** or, failing that, the backing field **`<ContactId>k__BackingField`** of a `ContactId` auto-property (both strings, found by reflection).
- Unknown types log `Clone <id>: unknown component`.
- `RadarCircleMover` keeps its id in a `ContactId` auto-property (default `"ContactA"`) and re-registers in `Configure`. Before 0.2.2 clones of it kept `ContactA`. It still logs `as a structural copy`, because no save record describes it.
- **Registry ids (0.2.2).** The game's `ContactRegistry` (rewritten in 26140834) maps a contact by the id it had when it last called `Register` (private `_idByContact`). On a client, ids that arrive by field sync never re-register, so `TryGetContact(id)` fails and position updates are dropped. `EntityReplicator.RepairRegistryIds` re-registers such contacts once a second (counted as `idfix=` in `[DIGEST]` lines). It uses the private `_contactsList` and `_idByContact`.
- **VFR flights (0.2.2).** A VFR flight is a contact only through its `OrbitMover`; `TakeoffTurnMover` is not an `IContact`. `RestoreVFRAircraft` rebuilds an airborne flight with `isInOrbit == false` as a fresh `VfrLocalSortieController` takeoff, which never becomes a contact on a puppet. The `Vfr` spawn sets `isInOrbit` (and a radius if there is none) to take the orbit branch. Field sync then sets the mover's real transit state.

**EntityReplicator:**
- `IsLocalAuthority` compares `GetType().Name` with the strings **`"SatelliteContact"`**, **`"SatelliteMover"`** and **`"RedArrowsJet"`** (26140834). If one is renamed, those contacts are both replicated and simulated locally.
- Red Arrows: `RedArrowsDisplayService` runs on every peer. It is deterministic from the shared timeline, except that a Critical tension tier recalls the team, and the client reads that tier from the replicated scores. A world join restores the team through the game's own `RedArrowsDisplayService.RestoreTeamAfterLoad`. Jet ids come from `RedArrowsCatalog.BuildContactId(number, idStamp)`, so both sides use the same ids and `TrackSync` can align track labels.
- Positions assume a 2D map in the XY plane, rotated around Z only.
- Contacts are found with `GameObject.Find(contactId)`, so a GameObject's name must equal its contact id.

### 5.8 World join (`Bootstrap/WorldSync.cs`)

**Game APIs used:**

| Area | Members |
|---|---|
| `PlayerSession` | `IsLoggedIn()`, `SecondsSinceLogin()`, `GetAor()`, `GetTheatre()`; `Login(Role.AirDefender, aor, theatre, startupSoundCompleted:, isLoadedGame:)` (the 5-parameter overload); enum `Theatre {UK, Falklands}` |
| `GameSaveManager` | `HasLoadedRecently()`, `BuildSnapshot()`, private `LoadFromPath(path, "Co-op session")`, `GetLastLoadedTheatre()`, `GetLastLoadedAor()` |
| `AorManager` | `Aor {None, North, South}`, parsed **by name** |
| Other | `TrainingSessionState.IsTraining`, `SfxService.SetMenuOnlyAudioMode(enabled:)`, `LoginScreenUI.instance` (private static), `Bootstrapper.ApplyTheatre`, `FirRegionUtility.InvalidateCache`, `GameResetService.ResetToMainMenu`, `PlayerProfileManager.SelectedProfile` |

**Save format:**
- The world is sent as `JsonUtility.ToJson(SaveBlob)`, gzipped, then written to `BepInEx\coop_tmp\coop_world_<pid>.json` on the client and loaded from there.
- The mod has no save-version check of its own. `LoadFromPath` → `UpgradeNamedSaveIfNeeded` accepts schema 0–3 and **throws above 3**.
- `LoadFromPath` calls `SteamCloudSaveManager.TryEnsureLocalUpToDate(path)` on the temporary path.
- Watch the `save` section for the two constants, and `il-hash` for `LoadFromPath`.

### 5.9 Tracks, radar, time and presentation

| Mod file | Game members |
|---|---|
| `TrackSync.cs` | `TrackManager.Instance`, `contactToTrack` (internal `Dictionary<string, ActiveTrack>`), `contactIdToSerial`, `nextSerialId`; `TrackManager.ActiveTrack.renderer/serialId/roleCode`; private `TrackRenderer` fields `identifier`, `color`, `callsign`, `spokenCallsign`; setters `SetIdentifierAndColor`, `SetTrackId`, `SetRole`, `SetCallsign`, `SetSpokenCallsign` |
| `RadarHitSync.cs` | A ground hit is "`NotifyHit` called **synchronously inside `RadarPrimaryHistory.Update`**". Other callers (missiles, AWACS/L11, datalink, ballistic, Fr24, MIRV, jamming, Santa, Trident; see `callers`) are forwarded by the host. If ground sweeps move to another method or a coroutine, clients would drop their own sweep hits. |
| `TimeSync.cs` | `GameTimeline.IsAvailable/GetTimelineSeconds/GetStartDateUtc/SetState`; private `TimeControlsUI.SetTimeScale`. Assumes `Time.fixedDeltaTime = 0.02 * scale` (a base step of 0.02 s). |
| `PresentationSync.cs` | `AsmaMessage` fields `messageId, simUtc, source, header, body, category, severity, requiresAcknowledge, teletype, options, devOnly` (category and severity sent as bytes); `AsmaMessageCategory.Coaching` is excluded; `AsmaOption.label/hotkey/isDefault/onSelected`; `AsmaService.Post`; `AsmaService._impl as AsmaServiceImpl` and its private `messages`, with a button chosen **by index**; private nested `VoiceAudioQueue.VoiceAudioRequest.clip/volume/isNormalized`; `VoiceAudioQueue.QueueGlobalVoiceClip`; `SfxServiceImpl`. Only clips loaded with `Resources.Load(string, Type)` can be forwarded.. Since 0.2.2 also: one-shot sounds through private `SfxServiceImpl.PlayInternal`/`PlayInternalNormalized` (clip path from `Resources.Load`, `AudioCategory` as a number), `AlertOverlay.Show` (private `instance.alerts`, `Alert.message`), `SessionBannerUI.ShowHudNotification`, `NuclearBlastEffect.Spawn` (its own sounds, `GlobalFlickerService` flicker and `AirwayFollowerMover.SetNuclearKeepOut`), `GameplayMusicCues.NotifyFriendlyShotDown`/`NotifyEasPlayed`. Sounds and banners are forwarded only when produced inside `SimulationGate.HostSimDepth` (to everyone) or while running a client's command (`CommandRouter.ExecutingRemote`, to that client). |
| `EndGameSync.cs` (0.2.2) | Private `ArmageddonGameEndService.instance` and its fields `gameEnded`, `victoryAchieved`, `armageddonTriggered`, methods `CacheDestroyedFeatures()`, `ShowEndGameUI(bool)`; `ArmageddonEndGameUI.isVisible`, `currentState`, enum `EndState { None, Defeat, Victory, SantaDefeat }`, `Hide()`, `ContinuePlaying()`, static `DisplaySantaDefeat()`; `ArmageddonGameEndService.EnterEndlessMode()`; `SfxService.ResumeAllAudio/StopSoundtrack`. Message `MsgType.EndGame` (53) and command `EndContinue`. |
| `UI/PartnerOverlay.cs` | `HookManager.GetAll()`, `TrackManager.GetTrackTransform`, `SfxService.PlaySelectTick/PlayMessageBeep`, `Camera.main` with the map on z = 0 |
| `Diagnostics/WorldDigest.cs` | Compares: timeline seconds and timescale; contact positions (excluding satellites, tolerance 3 units); `TrackManager.ForEachActiveTrack`, `GetSerialForContact`, `GetIdentifierForContact`; tension, performance and public scores; remaining **F3** count per base (private `Bootstrapper.EnsureManifestEntriesCache`, `s_manifestEntriesByBase`, `GetRemainingCountForBaseAndType`). It does **not** compare fuel, weapons, casualties, campaign state or track role/callsign. |
| `Diagnostics/AutoTest.cs` | Private `LoginScreenUI` members `instance`, `selectedRole`, `selectedAor`, `DoLogin`, `StartNewGame`, `startupSoundCompleted`; `PlayerProfileManager.Profiles/SelectProfile`; identifier strings `"P"`, `"A"`, `"U"`, `"F"`, contact prefix `"IFR"`, asset `"F3"` |

### 5.10 Session and Steam (`Session/*.cs`, `Net/SteamTransport.cs`)

**Handshake.**
- `GameFingerprint` is `typeof(GameSaveManager).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12)`.
- **Only `AirDefender.dll`'s code is fingerprinted.** An update that only changes assets or data keeps the same fingerprint (`check-game-update` warns about this).
- The Hello message is `Protocol.Version`, `Plugin.Version`, the fingerprint and the player name. The lobby stores `adcoop=1` and `adcoop_ver`.

**Game classes:** `SteamManager.Initialized` (the getter creates the SteamManager; its `Awake` calls `RestartAppIfNecessary(3985030)`), `PlayerProfileManager.SelectedProfile.callsign`, `PlayerSession`.

**Steamworks.NET calls:**
- `SteamNetworkingMessages`: Send, Receive (`SteamNetworkingMessage_t` read with `Marshal.PtrToStructure` + `Release`), Accept, Close.
- `SteamNetworkingUtils.InitRelayNetworkAccess` and `SetConfigValue`.
- `SteamMatchmaking`: CreateLobby, SetLobbyData, SetLobbyJoinable, GetLobbyData, GetLobbyOwner, GetNumLobbyMembers, GetLobbyMemberByIndex, InviteUserToLobby, JoinLobby, LeaveLobby.
- `SteamFriends`: persona, rich presence, overlay invite dialog, friend list.
- `SteamUtils.GetAppID`, `SteamUser.GetSteamID`.
- Callbacks: `GameLobbyJoinRequested_t`, `LobbyEnter_t`, `LobbyChatUpdate_t`, `LobbyCreated_t`, `SteamNetworkingMessagesSessionRequest_t`, `SteamNetworkingMessagesSessionFailed_t`.

**If Steamworks.NET changes, a rebuild is required.** Without one, expect `MissingMethodException`/`TypeLoadException`, or memory corruption in `Poll`, because the transport reads the message struct by memory layout.

The app id `3985030` is hard-coded in `install.ps1` (`steam_appid.txt`), `package/Install-AirDefenderCoop.ps1` and the README.

---

## 6. Environment

| Risk | Severity | Check |
|---|---|---|
| Game switches to IL2CPP (`GameAssembly.dll`, `il2cpp_data`, no `MonoBleedingEdge`) | **Blocker.** BepInEx 5 can't load at all. | `check-game-update` reports BLOCKER |
| Unity version change (now 6000.4.0f1) | High. BepInEx 5 has needed fixes for specific Unity versions before (the Unity 6 log writer, 2022.3.62f3). | Unity row in the report; `LogOutput.log` must show `Chainloader started` and the `Air Defender Co-op ... on Unity ...` line |
| Steamworks.NET / `steam_api64` change | High (see [5.10](#510-session-and-steam-sessioncs-netsteamtransportcs)) | Steamworks row and `manifest-diff.txt` |
| `Unity.InputSystem` / IMGUI change | Medium. The F9 panel hotkey and the overlay use `Keyboard.current` and `OnGUI`. | compile check |
| New save, cloud or prefs write paths | Low. They only matter for `--coop-sandbox` local tests, but those could then overwrite real profiles. Gaps today: direct `File.Delete`/`File.Copy` in `GameSaveManager`, `PlayerProfileManager`, `LeaderboardService`, `MapFeaturesStorage`, `PanelLayoutStorage`, `SubmarineLaunchController`; async `LocalFileStorage` writes; `SteamCloudSaveManager.SetCloudEnabledForApp`. | `callers` section |
| Renamed exe, data folder or process | Low | Scripts assume `Air Defender.exe`, `Air Defender_Data\Managed`, process name `Air Defender`, and Steam installdir `Air Defender` |
| Tooling | Low | Publicizer and RefDump need BepInEx's `Mono.Cecil.dll` (0.10.4) and the .NET 10 SDK; decompiling needs `ilspycmd` (11.1, `dotnet tool install -g ilspycmd`); packaging needs `.deps/BepInEx_win_x64_5.4.23.5.zip` |

---

## 7. Log markers and expected counts

`pwsh scripts/check-game-update.ps1 -Logs` does all of this. Use the table below when reading logs by hand.

**Logs:**
- `BepInEx\LogOutput.log`: everything, including HarmonyX warnings.
- `BepInEx\coop-<pid>.log`: the mod only, one file per process, with `[DIGEST]` and `[AUTOTEST]` lines.

**Expected startup lines:**
```
Air Defender Co-op <ver> on Unity <unity>, game <fingerprint>
SimulationGate: 69 frame methods gated, 9 tick owners     <- new expected value = refdump "SimulationGate TOTAL"
Scoring guards: 29                                        <- refdump "TOTAL guarded"
Command routes: 86                                        <- number of MethodRouter.Add + Reserve lines; stays 86 even when routes fail
```

There are **131 normal** HarmonyX warnings of the form `AccessTools.DeclaredMethod: Could not find method for type AirDefender.X and name LateUpdate|FixedUpdate`. They come from SimulationGate trying every frame method on every type.

**Failure markers:**

| Marker | Meaning |
|---|---|
| `Patching failed - co-op disabled` | A missing or ambiguous Harmony target. Co-op is off. |
| `SimulationGate: type X not found` / `tick type X not found` | A renamed simulation type; clients desync. |
| `Route: T.M not found` / `has unsupported parameter` / `could not patch` | A player action doesn't reach the host. |
| `Ambiguous match`, `Undefined target method`, `Null method for airdefender.coop` | Harmony target problems. |
| `AccessTools.Method: Could not find` | A lookup by name failed. |
| `Spawned X (T) as a structural copy` | No save-format record for that entity. Normal for RadarCircleMover and the heli shuttles; new types showing up here means SpawnCatalog needs a new kind. |
| `Clone X: unknown component` / `cannot add` / `could not add T from host` | Component sync problems. |
| `World load failed`, `Could not snapshot the world`, `the game refused to load the host's world`, `Unsupported named-save schema version` | World join is broken. |
| `game version mismatch` | Peers are on different builds. |
| `[AUTOTEST] FAIL`, `Partner call ... threw`, `Host could not run` | A scripted step or routed command failed. |
| `[DIGEST] DIFF` | Client world differs from the host's. About 80–87% of digests matched on v0.2.0; brand-new aircraft and tracks between sweeps account for the rest. |

---

## 8. Issues found while building this inventory

These already exist on build 25378621. They are not caused by an update; they're listed here so they aren't mistaken for update regressions.

1. **`RadarStation.lockedStationIds` is never synced.** It's allowlisted, but it is `static readonly HashSet<int>`, and FieldCodec only supports `HashSet<string>`.
2. **Other dropped state:**
   - `AssetInventory.startingByType` and `remainingByType` (`Dictionary<AircraftType,int>`)
   - `CriticalRaidOrchestrator._assignedCityTargets` (`HashSet<Vector3>`)
   - `EnemyCampaignDirector.lastPhase` (a nullable enum)
   - every field of `SessionStatisticsService`, so that target sends nothing
3. **Case-insensitive collections lose their comparer on clients.** FieldCodec rebuilds collections with the ordinal/default comparer. Affected: `TrackManager.enemyInsideFirPenaltyApplied` and `raidWarnedThroughLadder`, `DestroyedAirfieldService.destroyedBasesNormalized`, `BloodhoundSamService._sitePowerOverrides` and `_contactLocks`.
4. **SpawnCatalog has already drifted from the game's restore code:**

   | Area | Missing compared to the game |
   |---|---|
   | Stationed | the `MapFeaturesStorage` position fallback; the `{type}_{base}_{j}` default name |
   | AWACS | `TrackRenderer.SetCallsign`, and the game adds `ManifestTag` after the mover |
   | Nimrod | the callsign source, and component order |
   | Parked tanker | `TankerAsset` configure |
   | Airborne tanker | `EnsureEwTrackImmediate` + `SetRole("T")`/`SetClassification("K")`/`SetCallsign` |
   | TankerTrack | the empty-`modelId` follow-up |
5. **Interceptor pairing.** `RestoreActiveInterceptors` resolves `pairedMateId` only within the list it is given. Sending one record at a time loses pairing between mates that spawn in separate records.

---

## 9. Update history

### Mod 0.2.2 (`Protocol.Version` 4), same game build 26140834

The six "not yet replicated" items, plus two fixes found while testing them:
- **Effects:** nuclear blasts, sounds, alert banners and music cues from the host's simulation (`PresentationSync`, `SimulationGate.HostSimDepth`).
- **End-of-game debrief:** `EndGameSync`.
- **Panel mirrors** (`PanelMirrors` via `CustomGlobals`): pending QRA, Hercules and Chinook flights, radio channels. The training-sortie labels travel with the radio channels, and `TrgProficiencyStore` is a global target.
- **Route ids 0–1 reserved:** `LaunchForTrack`/`LaunchForContact` only open the QRA dialog, so it now opens on the requesting player's screen instead of the host's.
- **QRA dialog after a hostile identification:** it opens on the identifying client, and the host no longer opens it.
- **`NuclearDetonationWatcher` gated:** 69 frame methods.
- **VFR flights and registry ids:** VFR flights flying out to their orbit never appeared on clients, and `RadarCircleMover` contacts (VFR cross-country flights) stopped moving on clients because their ids never re-registered (see [5.7](#57-entity-spawns-replicationspawncatalogcs-entityreplicatorcs)). Both bugs predate this release; short quiet test sessions had no VFR traffic. A 3-client test with VFR traffic went from 29–34% to 96% digest matches.

Gotchas found:
- **`JsonUtility` and this mod's classes:** it serialised a `List<>` of this mod's own nested `[Serializable]` class as empty (with no error). Mirrors use parallel lists of primitives or of game types.
- **QRA countdown in autotests:** the game's QRA launch sequence waits for player responses before it starts its countdown (the same happens in single player, see the `solo-qra` autotest). `host-events` therefore starts the countdown itself with `SetPendingLaunchEta`.


### 25378621 → 26140834 (mod 0.2.1, `Protocol.Version` 3)

The check reported 0 compile errors and one `MISSING(signature changed)`: `GameSaveManager.RestoreVFRAircraft` gained an optional `civilRestoredIds` parameter, which a rebuild picks up. With `null` it behaves as before. The game content added in this update, and what the mod does about it:

| New in the game | Co-op handling |
|---|---|
| Air combat training sorties (`TrgSortieService`, `TrgSortieController`, `QRAInterceptorMover` training phase, TRG credit and proficiency) | `TrgSortieController` gated; `TryStart` and `TryLaunchFromBase` routed (ids 84 and 85); `OnTrainingSortieCredited` guarded by the naming convention. Clients' panels don't show sortie progress. |
| Red Arrows display team (`RedArrowsDisplayService`, `RedArrowsJet`) | Local authority on every peer, like satellites; NOTAMs de-duplicated. |
| Chaff (`ChaffCloudRegistry`, `EnemyChaffService`, `QRADefensiveService`) | Registry mirrored through `CustomGlobals` `ChaffClouds`. |
| Pause when the window loses focus (`TimeControlsUI.HandleFocusChanged`) | Disabled while co-op is on; otherwise one player alt-tabbing pauses everyone. |
| Numbered CAP anchors (`CapNumberRegistry`, `CAPAnchorState.capNumber`) | `SpawnCatalog` claims the host's number before creating the anchor. |
| Batch identification of hooked tracks (`IdentBatchConfirmUI`) | Goes through the routed `TrySetOperatorClassification`, one track at a time; nothing to do. |
| Music cues, background radio channels, training-area overlay | Presentation only, so left local. Music the host's simulation triggers isn't forwarded. |

Unchanged: Unity 6000.4.0f1, Mono, Steamworks.NET 2025.162.1 (`SteamNetworkingMessage_t` decompiles identically), `steam_api64.dll`, save schema 3.

Tooling fixes made during this update:
- `run-local-test.ps1` waits for each instance's plugin to load before starting the next. On this build, a client started 3 s after the host hit a sharing violation on `BepInEx\config\BepInEx.cfg`, and BepInEx failed to load in it.
- `check-game-update.ps1` reports a successful compile, and downgrades `MISSING(signature changed)` when the rebuild succeeds.
- `MethodRouter` only treats a returned or out object as a new entity when it's a contact, so `TrgSortieController` doesn't trigger a `No spawn record` warning.
