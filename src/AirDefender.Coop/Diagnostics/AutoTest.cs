using System;
using System.Collections.Generic;
using System.Linq;
using AirDefender;
using UnityEngine;

namespace AirDefenderCoop.Diagnostics
{
    /// <summary>
    /// "--coop-autotest &lt;script&gt;" drives an instance through the menus without clicks so a
    /// two-instance test can run unattended. Each script is a list of timed steps; results are
    /// written to the coop log with an "[AUTOTEST]" prefix.
    /// </summary>
    public static class AutoTest
    {
        private sealed class Step
        {
            public string Name;
            public Func<bool> Ready = () => true; // wait until true (or timeout)
            public Action Run;
            public float Delay;                    // seconds to wait after Ready
            public float Timeout = 120f;
        }

        private static List<Step> _steps;
        private static int _index;
        private static float _stepStart = -1f;
        private static float _readyAt = -1f;

        public static bool Active => _steps != null && _index < _steps.Count;

        public static void Start(string script)
        {
            _steps = new List<Step>();
            _steps.Add(new Step { Name = "wait for login screen", Ready = () => LoginScreenUI.instance != null, Delay = 3f });
            _steps.Add(new Step { Name = "select profile", Run = SelectProfile, Delay = 1f });

            switch (script)
            {
                case "host":
                case "basic":
                    _steps.Add(new Step { Name = "start new UK game", Run = StartNewGame, Delay = 1f });
                    _steps.Add(new Step { Name = "wait for login", Ready = PlayerSession.IsLoggedIn, Delay = 5f });
                    break;
                case "client":
                    break;
                case "client-cmds":
                    AddClientCommandChecks();
                    break;
                case "host5x":
                    _steps.Add(new Step { Name = "start new UK game", Run = StartNewGame, Delay = 1f });
                    _steps.Add(new Step { Name = "wait for login", Ready = PlayerSession.IsLoggedIn, Delay = 5f });
                    _steps.Add(new Step { Name = "wait for partner world", Ready = () => Bootstrap.WorldSync.PartnerWorldReady, Delay = 2f });
                    _steps.Add(new Step { Name = "speed x5", Run = () => SetSpeed(5f) });
                    break;
                case "observe":
                    _steps.Add(new Step { Name = "start new UK game", Run = StartNewGame, Delay = 1f });
                    _steps.Add(new Step { Name = "wait for login", Ready = PlayerSession.IsLoggedIn, Delay = 5f });
                    _steps.Add(new Step { Name = "speed x5", Run = () => SetSpeed(5f) });
                    _steps.Add(new Step { Name = "dump 1", Run = DevDump.All, Delay = 60f });
                    _steps.Add(new Step { Name = "dump 2", Run = DevDump.All, Delay = 120f });
                    break;
                default:
                    CoopLog.Warn($"[AUTOTEST] unknown script '{script}', only selecting a profile");
                    break;
            }
            Extra?.Invoke(script, _steps.Count);
            CoopLog.Info($"[AUTOTEST] script '{script}' with {_steps.Count} steps");
        }

        /// <summary>Lets other modules append scripted checks (name, insert position).</summary>
        public static event Action<string, int> Extra;

        public static void Add(string name, Func<bool> ready, Action run, float delay = 0f, float timeout = 120f)
        {
            _steps?.Add(new Step { Name = name, Ready = ready ?? (() => true), Run = run, Delay = delay, Timeout = timeout });
        }

        public static void Tick()
        {
            if (!Active) return;
            float now = Time.realtimeSinceStartup;
            var s = _steps[_index];
            if (_stepStart < 0f) _stepStart = now;

            if (_readyAt < 0f)
            {
                bool ready;
                try { ready = s.Ready(); }
                catch (Exception e) { ready = false; CoopLog.Warn($"[AUTOTEST] {s.Name} readiness check threw: {e.Message}"); }
                if (ready) _readyAt = now;
                else if (now - _stepStart > s.Timeout)
                {
                    CoopLog.Error($"[AUTOTEST] FAIL timeout waiting: {s.Name}");
                    Next();
                    return;
                }
                else return;
            }

            if (now - _readyAt < s.Delay) return;
            try
            {
                s.Run?.Invoke();
                CoopLog.Info($"[AUTOTEST] step ok: {s.Name}");
            }
            catch (Exception e)
            {
                CoopLog.Error($"[AUTOTEST] FAIL {s.Name}: {e}");
            }
            Next();
        }

        private static void Next()
        {
            _index++;
            _stepStart = -1f;
            _readyAt = -1f;
            if (!Active) CoopLog.Info("[AUTOTEST] script finished");
        }

        private static void SelectProfile()
        {
            var profiles = PlayerProfileManager.Profiles.Where(p => p != null).ToList();
            if (profiles.Count == 0) throw new Exception("no player profiles exist - create a callsign once in the normal game");
            var pick = profiles.FirstOrDefault(p => string.Equals(p.callsign, CoopConfig.CliProfile, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
            if (!PlayerProfileManager.SelectProfile(pick.id)) throw new Exception("SelectProfile refused " + pick.callsign);
            var ui = LoginScreenUI.instance;
            ui.selectedRole = PlayerSession.Role.AirDefender;
            ui.selectedAor = AorManager.Aor.North;
            try { SfxService.StopStartupSound(); } catch { }
            ui.DoLogin();
            CoopLog.Info($"[AUTOTEST] profile {pick.callsign} selected");
        }

        // ---------------------------------------------------------------- client command checks

        private static string _identTarget;
        private static QRAInterceptorMover _launched;
        private static string _launchedId;

        private static void AddClientCommandChecks()
        {
            _steps.Add(new Step { Name = "wait for host world", Ready = () => ClientGate.PuppetActive, Delay = 20f, Timeout = 180f });

            // 1. Speed change requested by the client must come back from the host.
            _steps.Add(new Step { Name = "client requests speed x2", Run = () => SetSpeed(2f) });
            _steps.Add(new Step
            {
                Name = "speed x2 applied by host", Ready = () => Mathf.Abs(Time.timeScale - 2f) < 0.01f, Timeout = 10f,
                Run = () => CoopLog.Info("[AUTOTEST] PASS speed change round trip")
            });

            // 2. Identify a track from the client.
            _steps.Add(new Step
            {
                Name = "client identifies a track", Ready = () => PickIdentTarget() != null, Timeout = 120f,
                Run = () =>
                {
                    _identTarget = PickIdentTarget();
                    CoopLog.Info($"[AUTOTEST] identifying {_identTarget} as F");
                    TrackManager.Instance.TrySetOperatorClassification(_identTarget, "F", new Color(0f, 1f, 1f));
                }
            });
            _steps.Add(new Step
            {
                Name = "identity confirmed by host", Timeout = 10f,
                Ready = () => TrackManager.GetRendererForContact(_identTarget)?.GetIdentifier() == "F",
                Run = () => CoopLog.Info($"[AUTOTEST] PASS identify round trip ({_identTarget} = F)")
            });

            // 3. Launch an interceptor from a base: a blocking call that must hand back a real aircraft.
            _steps.Add(new Step
            {
                Name = "client launches an F3", Delay = 2f,
                Run = () =>
                {
                    string baseName = FindBaseWith("F3");
                    if (baseName == null) throw new Exception("no base with an F3 available");
                    _launched = Bootstrapper.SpawnInterceptorAtBase(baseName, "F3");
                    _launchedId = _launched != null ? _launched.ContactId : null;
                    CoopLog.Info($"[AUTOTEST] SpawnInterceptorAtBase({baseName}) returned {(_launched != null ? _launchedId : "null")} in {Commands.MethodRouter.LastBlockMs:0} ms");
                    if (_launched == null) throw new Exception("host returned no aircraft");
                }
            });
            _steps.Add(new Step
            {
                Name = "task the new F3 to identify", Delay = 1f,
                Run = () =>
                {
                    string target = PickIdentTarget() ?? _identTarget;
                    _launched.AssignIdentTask(target, false);
                    CoopLog.Info($"[AUTOTEST] AssignIdentTask({target}) sent for {_launchedId}");
                }
            });
            _steps.Add(new Step
            {
                Name = "launched F3 still mirrored", Delay = 10f,
                Ready = () => true,
                Run = () =>
                {
                    bool exists = ContactRegistry.TryGetContact(_launchedId, out _);
                    CoopLog.Info($"[AUTOTEST] {(exists ? "PASS" : "FAIL")} launched aircraft {_launchedId} present on client");
                }
            });
            _steps.Add(new Step { Name = "client requests speed x5", Run = () => SetSpeed(5f) });
        }

        private static string PickIdentTarget()
        {
            var tm = TrackManager.Instance;
            if (tm == null) return null;
            foreach (var kv in tm.contactToTrack)
            {
                var r = kv.Value?.renderer;
                if (r == null) continue;
                string id = r.GetIdentifier();
                if ((id == "P" || id == "A" || id == "U") && kv.Key.StartsWith("IFR") && kv.Key != _identTarget) return kv.Key;
            }
            return null;
        }

        private static string FindBaseWith(string assetType)
        {
            Bootstrapper.EnsureManifestEntriesCache();
            if (Bootstrapper.s_manifestEntriesByBase == null) return null;
            foreach (var kv in Bootstrapper.s_manifestEntriesByBase)
            {
                try { if (Bootstrapper.GetRemainingCountForBaseAndType(kv.Key, assetType) > 0) return kv.Key; }
                catch { }
            }
            return null;
        }

        public static void SetSpeed(float scale)
        {
            var ui = UnityEngine.Object.FindAnyObjectByType<TimeControlsUI>();
            if (ui != null) ui.SetTimeScale(scale);
            else Time.timeScale = scale;
        }

        private static void StartNewGame()
        {
            var ui = LoginScreenUI.instance;
            LoginScreenUI.startupSoundCompleted = true;
            ui.StartNewGame();
        }
    }
}
