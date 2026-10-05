using AirDefender;
using AirDefenderCoop.Bootstrap;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop.Patches
{
    /// <summary>Session-lifecycle hooks: survive menu resets, follow host loads, block client saves.</summary>
    [HarmonyPatch]
    internal static class LifecyclePatches
    {
        // Returning to the main menu destroys every DontDestroyOnLoad root except Steam's,
        // which would take the BepInEx manager (and this plugin) with it.
        [HarmonyPatch(typeof(GameResetService), nameof(GameResetService.IsProcessLifetimePlatformRoot))]
        [HarmonyPostfix]
        private static void KeepModRoots(GameObject root, ref bool __result)
        {
            if (__result || root == null) return;
            if (root.GetComponent<BaseUnityPlugin>() != null || root.name == "BepInEx_Manager") __result = true;
        }

        [HarmonyPatch(typeof(GameResetService), nameof(GameResetService.ResetToMainMenu))]
        [HarmonyPrefix]
        private static void OnResetToMenu()
        {
            if (CoopSession.IsHost) WorldSync.HostReturnedToMenu();
            else if (CoopSession.IsClient && CoopSession.Connected && !ClientGate.InAllowedScope)
            {
                // The client chose to leave the shared world.
                CoopLog.Info("Client returned to the menu; leaving the co-op session");
                SteamLobbyService.Leave();
                CoopSession.Stop("partner returned to the menu");
            }
        }

        // Host loads a save mid-session: the client must get the new world.
        [HarmonyPatch(typeof(GameSaveManager), nameof(GameSaveManager.LoadFromPath))]
        [HarmonyPrefix]
        private static bool BeforeLoad(ref bool __result)
        {
            if (CoopSession.IsClient && CoopSession.Connected && !WorldSync.ApplyingRemoteWorld)
            {
                Ui.CoopToast.Show("Only the host can load a game during co-op");
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(GameSaveManager), nameof(GameSaveManager.LoadFromPath))]
        [HarmonyPostfix]
        private static void AfterLoad(bool __result)
        {
            if (__result && CoopSession.IsHost && CoopSession.Connected) WorldSync.RequestResend("host loaded a save");
        }

        // The client's world belongs to the host; saving it locally would only create confusing files.
        [HarmonyPatch(typeof(GameSaveManager), nameof(GameSaveManager.SaveAs))]
        [HarmonyPrefix]
        private static bool BeforeSave(ref bool __result)
        {
            if ((CoopSession.IsClient && CoopSession.Connected) || CoopConfig.Sandbox)
            {
                if (!CoopConfig.Sandbox) Ui.CoopToast.Show("Only the host can save during co-op");
                __result = false;
                return false;
            }
            return true;
        }

        // Starting directly from the exe must not bounce through Steam into the registered install.
        [HarmonyPatch(typeof(Steamworks.SteamAPI), nameof(Steamworks.SteamAPI.RestartAppIfNecessary))]
        [HarmonyPrefix]
        private static bool NoRestart(ref bool __result)
        {
            __result = false;
            return false;
        }
    }
}
