# Air Defender Co-op — online co-op mod for up to 4 players (BepInEx 5)

An unofficial **co-op mod for [Air Defender](https://store.steampowered.com/app/3985030/)**
(ROTOR3, Steam app 3985030), the Cold War UK air-defence radar/command simulator. One player
hosts; up to three Steam friends join with normal Steam invites, and everyone works the **same live
air picture**: identifying tracks, scrambling QRA, tasking fighters, tankers and AWACS, and running
the battle together. The player limit is a host setting (default 4, up to 16).

> **Status: v0.2.0, early test build.** Two PCs have connected over Steam peer-to-peer (v0.1.0).
> Sessions with more than two players have only been tested locally, with several game instances on
> one PC over loopback. Expect rough edges. See [Known limitations](#known-limitations).

Not affiliated with or endorsed by ROTOR3 / the developers of Air Defender. You need your own copy of the
game. This repository contains **no game files**.

---

## Features

- **Steam invites:** the host opens the F9 panel and creates a friends-only Steam lobby sized to the
  player limit. Friends can
  be invited through the Steam overlay or from a friend list inside the panel, which doesn't need
  the overlay. Accepting the invite starts or joins the game automatically (`+connect_lobby` is
  supported).
- **Peer-to-peer over Steam Datagram Relay:** this uses `ISteamNetworkingMessages`, so there are no
  open ports and no port forwarding.
- **Direct IP / LAN fallback:** a TCP connection, which is also used for local testing.
- **Joins any running session:** the host's live world (new game or loaded save) is streamed to
  each joining player and loaded through the game's own load path. Players already in the game
  carry on while someone joins.
- **Every player has full control.** Anything a client does is carried out by the host's
  simulation:
  - identify / classify tracks
  - QRA launches and tasking (identify, engage, escort, RTB, refuel, speed mode)
  - launching from bases and redeploying
  - CAP pairs; AWACS, Nimrod and tanker tasking
  - Hercules and Chinook supply
  - airspace closure, Bloodhound SAMs, datalinks, radar power
  - sim speed and pause
- **Synchronised world:**
  - every aircraft and missile, with positions, fuel, weapons, phases and missions
  - radar tracks: serial numbers, identities, colours, roles and callsigns
  - scores, casualties, base fuel, weapons and aircraft inventories
  - campaign progress, destroyed airfields and features, radar power, the operational clock
- **Shared radio and console:**
  - the host's ASMA console messages, radio voice traffic and alerts are forwarded to every client
  - messages and sounds that a client also produces on its own are de-duplicated
  - buttons on forwarded ASMA tasking messages work for clients
- **Player awareness:**
  - every other player's cursor, callsign and hooked tracks are shown in that player's colour
  - middle-click drops a ping marker on every screen
  - the F9 panel lists everyone with their colour and ping, and the host can remove a player
- **Safe session handling:**
  - the game build and mod version are checked on connect, and a full session refuses new players
  - if a client leaves, everyone else carries on; if the host leaves, everyone returns to the main menu
  - only the host saves and loads
  - training missions stay single-player

## Install (players)

1. Download the release zip (or build it, see below) and close Air Defender.
2. Run **`Install.bat`**. It finds your Steam copy of the game and installs BepInEx 5.4.23.5 plus the plugin.
3. Start the game from Steam.

Uninstall: `powershell -ExecutionPolicy Bypass -File Install-AirDefenderCoop.ps1 -Uninstall`

Every player needs their own copy of the game on **the same game update**, with this mod installed.

The player limit is `MaxPlayers` under `[Network]` in `BepInEx\config\airdefender.coop.cfg` on the
host (default 4, range 2–16). Each extra player adds host upload, so raise it only with a good
connection.

## How to play

| Host | Other players |
|---|---|
| Log in with your callsign, press **F9**, click **Host co-op** | Accept the Steam invite |
| Invite friends from the panel or overlay | Pick your callsign at the login screen |
| Start a new UK game or load a save | You load straight into the host's world, even mid-game |

Press **F9** for the co-op panel at any time. It shows connection state and the player list with pings.
Middle-click to ping.

## How it works

The host runs the real simulation. Every client runs the game as a **puppet**: its own simulation
decisions are switched off and its world mirrors the host's. Clients talk only to the host; the
host forwards cursors and pings between them.

| Layer | Source | Summary |
|---|---|---|
| Transport | `src/.../Net` | `SteamTransport` (Steam Datagram Relay P2P, one session per client) and `TcpTransport` (one connection per client), with length-prefixed binary messages |
| Session | `src/.../Session` | Per-player handshake (protocol, mod version, game build fingerprint, free slot), heartbeats and timeouts, player slots and the player list; the Steam lobby and invites |
| World join | `Bootstrap/WorldSync.cs` | Host captures the world with the game's `GameSaveManager.BuildSnapshot()` and gzip-streams it to the joining player only; the client loads it via `LoadFromPath` + `PlayerSession.Login` |
| Puppet mode | `Puppet/` | Harmony prefixes switch off 67 per-frame simulation methods (spawners, campaign directors, movers, AI, weapons) and 9 tick-scheduled systems on each client; 28 scoring entry points become host-only |
| Entities | `Replication/EntityReplicator.cs`, `SpawnCatalog.cs` | `IContact.ContactId` is the network id. New aircraft are rebuilt from the game's own per-entity save records and restore code, with a structural-copy fallback. Positions stream at 5 Hz with dead reckoning. A player joining mid-game is sent whatever spawned or despawned since their snapshot, and field state is resent once to everyone. |
| State | `StateReplicator.cs`, `FieldCodec.cs`, `GlobalTargets.cs`, `CustomGlobals.cs` | Reflection-based per-field delta sync of every game component on every aircraft, plus global services (scoring, base stocks, casualties, campaign, radar stations, track serials, base inventory) |
| Tracks | `TrackSync.cs`, `RadarHitSync.cs` | Ground radar sweeps run locally on both sides, so tracks move in step with each player's own sweep. Track labels are host-authoritative. Non-ground hits (Link 11, datalinks, missiles, jamming) are forwarded. |
| Time | `TimeSync.cs` | The host owns speed, pause and the operational timeline |
| Commands | `Commands/` | 84 game methods plus identification, speed and ASMA-option commands are routed from clients to the host, which runs them in arrival order. Calls that return a result block for one round trip so the game's UI continues with the host's real outcome (e.g. a freshly launched fighter); the result goes back to the caller only. |
| Presentation | `PresentationSync.cs`, `UI/` | ASMA, voice and alert forwarding; F9 panel; player overlay |
| Diagnostics | `Diagnostics/` | Per-process logs, a host-vs-client world digest every 5 s on every client, scripted autotests, entity dumps (Ctrl+F10) |

### Measured locally (two instances, one PC)

- **Join:** world transfer and load took about 150 ms.
- **6-minute soak at 5x speed, up to 158 aircraft:**
  - the host-vs-partner digest matched in 63 of 72 checks; every mismatch was a brand-new aircraft
    still within its 0.35 s spawn delay
  - positions within 0.4 map units
  - track serials and identities, scores and base inventory identical
  - clock within ±0.2 s
  - no errors
- **Commands from the partner:**
  - speed change and identification round trips succeeded
  - a fighter launched from Leuchars was returned to the partner in 21 ms
- **Host cost:** about 2–3% CPU and 20–100 KB/s upload, depending on traffic.

### Measured locally (four instances, one PC, v0.2.0)

- **Joins:** a host and three clients. The second and third clients joined 25 s and 50 s into a
  running game at 5x speed. Each joiner got its own world (about 30 KB, loaded in about 170 ms),
  then 3–4 catch-up spawns and 8 stale contacts removed. Players already in the game were not
  interrupted.
- **3-minute session, up to about 100 aircraft:**
  - 107 of 135 digests matched across the three clients. The mismatches were brand-new aircraft
    within their spawn delay, a track or two between sweeps, one digest taken during a join, and
    position jumps that appeared on every client at the same moment
  - scores, track serials and identities matched; the clock was within 0.3 s
  - no errors
- **Commands:** all three clients ran the scripted speed, identification and fighter-launch checks.
  All passed, and each launched fighter was returned to the client that asked for it.
- **Host upload:** about 13 KB/s with one client in the world, 32 KB/s with two and 79 KB/s with
  three (peak 115 KB/s).
- **Leaving:** a client process was killed mid-game. The host removed it within a second, and the
  remaining client stayed in sync.

## Known limitations

- **Limited internet testing.** Steam P2P between two PCs works, but long sessions over the internet
  have not been measured yet, and sessions of three or more have only run on one PC.
- **Host upload grows with each player.** The host sends every client its own copy of the stream
  (roughly 20–100 KB/s each, plus a burst of a few hundred KB when someone joins).
- **Simultaneous orders:** if two players task the same aircraft at once, the later order wins.
- **UK campaign only so far.** The Falklands theatre is untested, and training missions are blocked
  in co-op.
- **Not yet replicated:**
  - shoot-down and nuclear visual effects
  - the end-of-game screen
  - pending-QRA countdowns in clients' panels
  - supply-flight lists
- **Fixed host:** only the host can save or load, and the session ends if the host quits.
- **Game updates:** a game update will likely require a rebuild. The handshake refuses mismatched
  builds.

## Building from source

Requirements: Windows, .NET SDK 10, and Air Defender with BepInEx 5.4.23.5 (win x64) in the parent folder of this repo.

```
pwsh scripts/build.ps1      # makes a compile-time publicized copy of AirDefender.dll in lib/, builds the plugin
pwsh scripts/install.ps1    # copies the plugin into ../BepInEx/plugins/AirDefenderCoop
pwsh scripts/package.ps1    # artifacts/AirDefenderCoop-<version>.zip (BepInEx + plugin + installer)
```

`lib/`, `artifacts/` and any decompiled game code are git-ignored and never committed.

### Local multi-instance test

```
pwsh scripts/run-local-test.ps1 -HostScript host5x -ClientScript client-cmds -Seconds 120
pwsh scripts/run-local-test.ps1 -HostScript host5x -ClientScript client-cmds -Clients 3 -JoinStagger 25 -Seconds 200
```

This starts a host and one or more `--coop-sandbox` clients on loopback, arranged on screen.
`-JoinStagger` spaces out the clients so later ones join a running game. The sandbox stops the
extra instances from writing preferences, saves or Steam Cloud. Scripted steps run commands from
each client, and `[DIGEST]` lines in each client's `BepInEx/coop-<pid>.log` compare its world with
the host's.

Command-line flags:

| Flag | Purpose |
|---|---|
| `--coop-host [local\|steam]` | start hosting on launch |
| `--coop-join <ip[:port]>` | join a direct (TCP) host on launch |
| `--coop-port <n>` | TCP port for direct play |
| `--coop-max-players <n>` | player limit for this run only (not saved to the config) |
| `--coop-sandbox` | the second local instance keeps every write in memory |
| `--coop-autotest <script>` | run a scripted test |
| `--coop-profile <callsign>` | callsign the autotest selects |

## Reporting problems

Please attach logs from **every** PC: `<game>\BepInEx\LogOutput.log` and the newest
`<game>\BepInEx\coop-*.log`. Also say which machine was the host.

## Credits

- [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX)
- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET), as shipped with the game
- Air Defender by ROTOR3
