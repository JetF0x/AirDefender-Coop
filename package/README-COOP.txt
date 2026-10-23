AIR DEFENDER CO-OP (up to 4 players)
====================================

Every player needs: their own copy of Air Defender on Steam, the same game update, and this mod.

INSTALL
  1. Close Air Defender.
  2. Double-click "Install.bat" (it finds your Steam copy of the game automatically).
     If it cannot, run in PowerShell:
       powershell -ExecutionPolicy Bypass -File Install-AirDefenderCoop.ps1 -GameDir "D:\...\Air Defender"
  3. Start Air Defender from Steam as normal.

PLAY
  Host:
    1. Start the game, log in with your callsign.
    2. Press F9 and click "Host co-op".
    3. Invite up to 3 friends from the list (or with the Steam overlay).
    4. Start a new UK game or load a save. Friends join your world automatically,
       including friends who join after the game has started.
  Friends:
    1. Accept the Steam invite (the game starts if it is not running).
    2. Pick your callsign at the login screen - you then join the host's world.

  Every player has full control: identify tracks, launch and task QRA, tankers, AWACS,
  supply flights, airspace closure, radar power, speed and pause. Orders from either
  player are carried out in the host's world and everyone sees the results.
  Middle mouse button: drop a ping marker that everyone sees.
  Each player's cursor, callsign and hooked tracks are shown in their own colour
  (the F9 panel lists everyone with their colour and ping; the host can remove players).

  Only the host saves and loads. If the host leaves, everyone returns to the menu.
  The player limit is MaxPlayers in BepInEx\config\airdefender.coop.cfg (host only;
  default 4). More players need more upload bandwidth on the host.
  Training missions are single-player.

UNINSTALL
  powershell -ExecutionPolicy Bypass -File Install-AirDefenderCoop.ps1 -Uninstall

TROUBLESHOOTING
  Logs: <game folder>\BepInEx\LogOutput.log and <game folder>\BepInEx\coop-<number>.log
  Please send both logs from every PC when reporting a problem.
