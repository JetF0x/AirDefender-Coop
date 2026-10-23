using System;
using BepInEx.Configuration;
using UnityEngine;

namespace AirDefenderCoop
{
    /// <summary>Settings from the BepInEx config file plus command-line overrides.</summary>
    public static class CoopConfig
    {
        public static ConfigEntry<int> Port;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<KeyCode> PanelKey;
        public static ConfigEntry<bool> Verbose;
        public static ConfigEntry<string> LastJoinAddress;

        public static bool VerboseLog => Verbose != null && Verbose.Value;

        /// <summary>The panel key for the game's Input System (legacy KeyCode names map by name).</summary>
        public static UnityEngine.InputSystem.Key PanelInputKey =>
            Enum.TryParse(PanelKey.Value.ToString(), true, out UnityEngine.InputSystem.Key k) ? k : UnityEngine.InputSystem.Key.F9;

        // Command line (see README). Parsed once at startup.
        public static string CliHostMode;      // "local" | "steam" | null
        public static string CliJoinAddress;   // "127.0.0.1:27515"
        public static ulong CliConnectLobby;   // Steam's +connect_lobby <id>
        public static string CliAutoTest;      // scripted test name
        public static bool Sandbox;            // second local instance: never persist anything
        public static string CliProfile;       // callsign to auto-select (testing)
        public static int CliMaxPlayers;       // overrides MaxPlayers for this run only (not saved)

        public static void Bind(ConfigFile cfg)
        {
            Port = cfg.Bind("Network", "Port", 27515, "TCP port used for direct-IP / local co-op.");
            MaxPlayers = cfg.Bind("Network", "MaxPlayers", 4,
                new ConfigDescription("Players in a hosted session, including the host. Each extra player adds host upload.",
                    new AcceptableValueRange<int>(2, 16)));
            LastJoinAddress = cfg.Bind("Network", "LastJoinAddress", "127.0.0.1", "Last address typed into the Join box.");
            PanelKey = cfg.Bind("UI", "PanelKey", KeyCode.F9, "Key that toggles the co-op panel.");
            Verbose = cfg.Bind("Diagnostics", "VerboseLog", false, "Write high-volume replication diagnostics to the coop log.");
            ParseArgs(Environment.GetCommandLineArgs());
        }

        private static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                bool nextIsValue = next != null && !next.StartsWith("-") && !next.StartsWith("+");
                switch (a.ToLowerInvariant())
                {
                    case "--coop-host":
                        CliHostMode = nextIsValue ? next.ToLowerInvariant() : "local";
                        if (nextIsValue) i++;
                        break;
                    case "--coop-join":
                        if (nextIsValue) { CliJoinAddress = next; i++; }
                        break;
                    case "--coop-port":
                        if (nextIsValue && int.TryParse(next, out int p)) { Port.Value = p; i++; }
                        break;
                    case "--coop-autotest":
                        CliAutoTest = nextIsValue ? next : "basic";
                        if (nextIsValue) i++;
                        break;
                    case "--coop-sandbox":
                        Sandbox = true;
                        break;
                    case "--coop-max-players":
                        if (nextIsValue && int.TryParse(next, out int mp)) { CliMaxPlayers = mp; i++; }
                        break;
                    case "--coop-profile":
                        if (nextIsValue) { CliProfile = next; i++; }
                        break;
                    case "+connect_lobby":
                        if (nextIsValue && ulong.TryParse(next, out ulong lobby)) { CliConnectLobby = lobby; i++; }
                        break;
                }
            }
        }
    }
}
