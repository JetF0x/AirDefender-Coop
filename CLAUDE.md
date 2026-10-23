# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A BepInEx 5 / HarmonyX plugin (`netstandard2.1`, C#) that adds online co-op (default 4 players, configurable 2–16) to the Unity game Air Defender. The repo lives **inside the game install**: `GameDir` (see `Directory.Build.props`) defaults to the parent folder, and the build references the game's `Air Defender_Data\Managed` and `BepInEx\core` DLLs from there. `README.md` holds the user-facing feature list, architecture table, measured results and known limitations.

Don't read or copy from the sibling `Air Defender Modded*` folders (earlier mod attempts). Derive everything from this install's game assemblies. `_decomp/AirDefender/` is a decompiled copy of the game code for reference. It is git-ignored and must never be committed.

## Commands (PowerShell 7, Windows)

```
pwsh scripts/build.ps1      # publicize game DLL into lib/ if stale, then dotnet build (Release)
pwsh scripts/install.ps1    # copy plugin into ../BepInEx/plugins/AirDefenderCoop (close the game first)
pwsh scripts/package.ps1    # build + artifacts/AirDefenderCoop-<version>.zip (BepInEx + plugin + installer)
```

- `lib/AirDefender.dll` is a **publicized** copy of the game assembly made by `tools/Publicizer` (Mono.Cecil). Every type, method and field is made public so the mod can call private game members directly. It is used only at compile time; the real game DLL loads at runtime. `build.ps1` regenerates it whenever the game DLL is newer, so a game update means you rebuild and then fix whatever broke.
- There are no unit tests. To verify a change, run real game instances over loopback:

```
pwsh scripts/run-local-test.ps1 -HostScript host5x -ClientScript client-cmds -Seconds 120
pwsh scripts/run-local-test.ps1 -HostScript host5x -ClientScript client-cmds -Clients 3 -JoinStagger 25 -Seconds 200
```

  This **kills any running `Air Defender` process**, runs `install.ps1`, starts a host and N `--coop-sandbox` clients, and with `-Seconds` prints every `BepInEx/coop-<pid>.log` at the end. Check those logs for `[DIGEST]` (client world vs host world), `[AUTOTEST]` step results and errors. The BepInEx log is `../BepInEx/LogOutput.log`. Autotest scripts (`host`, `host5x`, `client`, `client-cmds`, `observe`, and `host-events` with `client-events` for host-only presentation, end screen and panel mirrors, and `solo-qra`) are defined in `Diagnostics/AutoTest*.cs`. Run the event pair with `run-local-test.ps1 -HostScript host-events -ClientScript client-events -Seconds 240`. CLI flags (`--coop-host`, `--coop-join`, `--coop-max-players`, `--coop-sandbox`, `--coop-autotest`, `--coop-profile`) are parsed in `CoopConfig.cs` and listed in the README.

## Architecture

The host is authoritative, with a star topology: clients talk only to the host, and the host relays cursors and pings between them. `Plugin.cs` is the entry point. `Awake` applies every Harmony patch set and then `Init()`s each subsystem. `Update` calls `*.HostTick()` on the host and `*.ClientTick()` on clients. A new subsystem must be wired in at both places.

Key ideas that span several files:

- **Puppet clients.** `Puppet/SimulationGate.cs` uses Harmony prefixes to skip `Update`/`LateUpdate`/`FixedUpdate` and tick callbacks on clients. The skipped types are listed **by name as strings** in three lists: `EntityLogicTypes`, `WorldSimTypes` and `TickSimTypes`. They are resolved by reflection against the game assembly, so a renamed game type fails silently, not at compile time. Scoring entry points become host-only in `Patches/ReplicationPatches.cs`. Anything that makes simulation decisions on a client causes desync. Gate it, or replicate its result from the host.
- **Client actions = routed game methods.** `Commands/RouteList.cs` lists every game method a player can trigger. On a client, `MethodRouter` intercepts the call, serializes its args (`ArgCodec`) and runs it on the host. `blocking: true` routes wait one round trip so the client UI continues with the host's real return value. **The order of entries in `RouteList` is the wire ID, so only ever append to it.** `CommandRouter` handles the non-method commands (identification, speed, ASMA options).
- **World join.** `Bootstrap/WorldSync.cs` captures the host world with the game's own `GameSaveManager.BuildSnapshot()`, gzip-streams it to the joining peer only, and loads it through the game's load path. After that, `EntityReplicator` sends catch-up spawns and despawns, and field state is resent once to everyone.
- **Replication.** Entities are keyed by `IContact.ContactId`. `EntityReplicator`/`SpawnCatalog` rebuild new aircraft from the game's per-entity save records. `StateReplicator` + `FieldCodec` sync fields by reflection and delta: per component on aircraft, and for the global services listed in `GlobalTargets`/`CustomGlobals`. Tracks, radar hits, time and presentation (ASMA console, voice, alerts) each have their own `*Sync` class.
- **Transport/session.** `Net/ITransport` has two implementations: `SteamTransport` (ISteamNetworkingMessages P2P) and `TcpTransport` (direct IP, used by local tests). `Session/CoopSession.cs` owns the handshake, player slots, heartbeats and the `MsgType` handler registry (`CoopSession.Register`).
- **Sandbox.** `Patches/SandboxPatches.cs` (`--coop-sandbox`) keeps a second local instance's preferences, saves and Steam Cloud writes in memory so it doesn't clobber the host's data.

## Versioning and compatibility

- The handshake rejects peers unless **both** `Protocol.Version` (`Net/Protocol.cs`) and `Plugin.Version` match exactly. It also checks the game build fingerprint.
- Bump `Protocol.Version` whenever a message layout, `MsgType` value or `RouteList` order changes.
- A release version lives in two places that must stay equal: `Plugin.Version` in `Plugin.cs` and `<Version>` in `AirDefender.Coop.csproj`. `package.ps1` names the zip from the latter.
- `lib/`, `artifacts/`, `_decomp/`, `.deps/` (BepInEx zip used by packaging), `_baseline/`, `_update/`, `bin/` and `obj/` are git-ignored. No game files may be committed.

## Game updates

Follow `UPDATE.md`. In short: `scripts/snapshot-game-baseline.ps1` records the working build in `_baseline/<buildid>/`. `scripts/check-game-update.ps1` compares a new build (by default the Steam install, `..\..\Air Defender`) with that baseline and writes `_update/<buildid>/REPORT.md`. The report lists compile errors and a `tools/RefDump` diff of every game reference, including the string-only ones. Run `check-game-update.ps1 -Logs` after a local test to compare the startup counts and look for failure markers. `UPDATE.md` also lists every game reference the mod relies on and how to find it again in a new decompile, so update it whenever you add one.
