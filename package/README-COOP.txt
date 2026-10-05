AIR DEFENDER CO-OP (2 players)
==============================

Both players need: their own copy of Air Defender on Steam, the same game update, and this mod.

INSTALL
  1. Close Air Defender.
  2. Double-click "Install.bat" (it finds your Steam copy of the game automatically).
     If it cannot, run in PowerShell:
       powershell -ExecutionPolicy Bypass -File Install-AirDefenderCoop.ps1 -GameDir "D:\...\Air Defender"
  3. Start Air Defender from Steam as normal.

PLAY
  Host:
    1. Start the game, log in with your callsign.
    2. Press F9 and click "Host co-op (invite a Steam friend)".
    3. Invite your friend from the list (or with the Steam overlay).
    4. Start a new UK game or load a save. Your friend joins your world automatically.
  Friend:
    1. Accept the Steam invite (the game starts if it is not running).
    2. Pick your callsign at the login screen - you then join the host's world.

  Both players have full control: identify tracks, launch and task QRA, tankers, AWACS,
  supply flights, airspace closure, radar power, speed and pause. Orders from either
  player are carried out in the host's world and both see the results.
  Middle mouse button: drop a ping marker that your partner sees.
  Your partner's cursor and hooked tracks are shown in orange.

  Only the host saves and loads. If the host leaves, the friend returns to the menu.
  Training missions are single-player.

UNINSTALL
  powershell -ExecutionPolicy Bypass -File Install-AirDefenderCoop.ps1 -Uninstall

TROUBLESHOOTING
  Logs: <game folder>\BepInEx\LogOutput.log and <game folder>\BepInEx\coop-<number>.log
  Please send both logs from both PCs when reporting a problem.
