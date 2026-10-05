using System.Collections.Generic;
using AirDefender.SaveLoad.Storage;
using HarmonyLib;
using UnityEngine;

namespace AirDefenderCoop.Patches
{
    /// <summary>
    /// "--coop-sandbox": a second local test instance shares the first one's preferences, profiles
    /// and saves on disk. In sandbox mode every write stays in memory, so testing two instances on
    /// one PC can never corrupt the player's real data.
    /// </summary>
    internal static class SandboxPatches
    {
        private static readonly Dictionary<string, object> Prefs = new Dictionary<string, object>();
        private static readonly HashSet<string> Deleted = new HashSet<string>();

        public static void Apply(Harmony h)
        {
            if (!CoopConfig.Sandbox) return;
            var t = typeof(PlayerPrefs);
            void Pre(string name, System.Type[] args, string patch) =>
                h.Patch(AccessTools.Method(t, name, args), prefix: new HarmonyMethod(typeof(SandboxPatches), patch));

            Pre(nameof(PlayerPrefs.SetInt), new[] { typeof(string), typeof(int) }, nameof(SetInt));
            Pre(nameof(PlayerPrefs.SetFloat), new[] { typeof(string), typeof(float) }, nameof(SetFloat));
            Pre(nameof(PlayerPrefs.SetString), new[] { typeof(string), typeof(string) }, nameof(SetString));
            Pre(nameof(PlayerPrefs.GetInt), new[] { typeof(string), typeof(int) }, nameof(GetInt));
            Pre(nameof(PlayerPrefs.GetFloat), new[] { typeof(string), typeof(float) }, nameof(GetFloat));
            Pre(nameof(PlayerPrefs.GetString), new[] { typeof(string), typeof(string) }, nameof(GetString));
            Pre(nameof(PlayerPrefs.HasKey), new[] { typeof(string) }, nameof(HasKey));
            Pre(nameof(PlayerPrefs.DeleteKey), new[] { typeof(string) }, nameof(DeleteKey));
            Pre(nameof(PlayerPrefs.DeleteAll), System.Type.EmptyTypes, nameof(Skip));
            Pre(nameof(PlayerPrefs.Save), System.Type.EmptyTypes, nameof(Skip));

            var lfs = typeof(LocalFileStorage);
            h.Patch(AccessTools.Method(lfs, nameof(LocalFileStorage.WriteBytesAtomicSync)), prefix: new HarmonyMethod(typeof(SandboxPatches), nameof(Skip)));
            h.Patch(AccessTools.Method(lfs, nameof(LocalFileStorage.WriteTextAtomicSync)), prefix: new HarmonyMethod(typeof(SandboxPatches), nameof(Skip)));

            var rs = typeof(Steamworks.SteamRemoteStorage);
            h.Patch(AccessTools.Method(rs, nameof(Steamworks.SteamRemoteStorage.FileWrite)), prefix: new HarmonyMethod(typeof(SandboxPatches), nameof(FalseResult)));
            h.Patch(AccessTools.Method(rs, nameof(Steamworks.SteamRemoteStorage.FileDelete)), prefix: new HarmonyMethod(typeof(SandboxPatches), nameof(FalseResult)));
            CoopLog.Info("Sandbox mode: preferences, saves and Steam Cloud writes stay in memory");
        }

        private static bool Skip() => false;
        private static bool FalseResult(ref bool __result) { __result = false; return false; }

        private static void Store(string key, object v) { Prefs[key] = v; Deleted.Remove(key); }
        private static bool SetInt(string key, int value) { Store(key, value); return false; }
        private static bool SetFloat(string key, float value) { Store(key, value); return false; }
        private static bool SetString(string key, string value) { Store(key, value); return false; }

        private static bool GetInt(string key, int defaultValue, ref int __result)
        {
            if (Deleted.Contains(key)) { __result = defaultValue; return false; }
            if (Prefs.TryGetValue(key, out var v) && v is int i) { __result = i; return false; }
            return true;
        }

        private static bool GetFloat(string key, float defaultValue, ref float __result)
        {
            if (Deleted.Contains(key)) { __result = defaultValue; return false; }
            if (Prefs.TryGetValue(key, out var v) && v is float f) { __result = f; return false; }
            return true;
        }

        private static bool GetString(string key, string defaultValue, ref string __result)
        {
            if (Deleted.Contains(key)) { __result = defaultValue; return false; }
            if (Prefs.TryGetValue(key, out var v) && v is string s) { __result = s; return false; }
            return true;
        }

        private static bool HasKey(string key, ref bool __result)
        {
            if (Deleted.Contains(key)) { __result = false; return false; }
            if (Prefs.ContainsKey(key)) { __result = true; return false; }
            return true;
        }

        private static bool DeleteKey(string key) { Prefs.Remove(key); Deleted.Add(key); return false; }
    }
}
