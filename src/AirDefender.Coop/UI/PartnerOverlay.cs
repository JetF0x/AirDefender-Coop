using System.Collections.Generic;
using AirDefender;
using AirDefenderCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AirDefenderCoop.Ui
{
    /// <summary>
    /// Shows where the partner is looking: their cursor on the map, the tracks they have hooked,
    /// and "pings" (middle mouse button) that pulse on both screens.
    /// </summary>
    public static class PartnerOverlay
    {
        private const float SendInterval = 0.2f;
        private const float PingSeconds = 6f;

        private static float _nextSend;
        private static Vector2 _partnerCursor;
        private static bool _partnerCursorValid;
        private static float _partnerCursorTime;
        private static readonly List<string> PartnerHooks = new List<string>();
        private static readonly List<(Vector2 pos, float until, bool mine)> Pings = new List<(Vector2, float, bool)>();
        private static Texture2D _ring, _dot;
        private static GUIStyle _label;

        private static readonly Color PartnerColor = new Color(1f, 0.55f, 0.1f, 0.95f);

        public static void Init()
        {
            CoopSession.Register(MsgType.PartnerCursor, OnCursor);
            CoopSession.Register(MsgType.PingMarker, OnPing);
            CoopSession.PartnerLeft += _ => { _partnerCursorValid = false; PartnerHooks.Clear(); Pings.Clear(); };
        }

        private static bool InWorld => CoopSession.Connected && PlayerSession.IsLoggedIn() &&
                                       (CoopSession.IsHost ? Bootstrap.WorldSync.PartnerWorldReady : ClientGate.PuppetActive);

        public static void Tick()
        {
            if (!InWorld) return;
            var cam = Camera.main;
            var mouse = Mouse.current;
            if (cam == null || mouse == null) return;

            Vector2 screen = mouse.position.ReadValue();
            bool inside = Application.isFocused && screen.x >= 0 && screen.y >= 0 && screen.x <= Screen.width && screen.y <= Screen.height;
            Vector3 world = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -cam.transform.position.z));

            if (inside && mouse.middleButton.wasPressedThisFrame)
            {
                var p = new Vector2(world.x, world.y);
                Pings.Add((p, Time.realtimeSinceStartup + PingSeconds, true));
                CoopSession.Send(MsgType.PingMarker, w => w.Vec2(p));
                try { SfxService.PlaySelectTick(); } catch { }
            }

            float now = Time.realtimeSinceStartup;
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            List<string> hooks = null;
            try { hooks = HookManager.GetAll(); } catch { }
            CoopSession.Send(MsgType.PartnerCursor, w =>
            {
                w.Bool(inside);
                w.Vec2(new Vector2(world.x, world.y));
                int n = hooks == null ? 0 : Mathf.Min(hooks.Count, 8);
                w.U8((byte)n);
                for (int i = 0; i < n; i++) w.Str(hooks[i]);
            }, reliable: false);
        }

        private static void OnCursor(NetReader r)
        {
            _partnerCursorValid = r.Bool();
            _partnerCursor = r.Vec2();
            _partnerCursorTime = Time.realtimeSinceStartup;
            PartnerHooks.Clear();
            int n = r.U8();
            for (int i = 0; i < n; i++) PartnerHooks.Add(r.Str());
        }

        private static void OnPing(NetReader r)
        {
            var p = r.Vec2();
            Pings.Add((p, Time.realtimeSinceStartup + PingSeconds, false));
            try { SfxService.PlayMessageBeep(); } catch { }
        }

        private static void EnsureAssets()
        {
            if (_ring != null) return;
            _ring = MakeCircle(64, 0.82f, 1f);
            _dot = MakeCircle(16, 0f, 1f);
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            _label.normal.textColor = PartnerColor;
        }

        private static Texture2D MakeCircle(int size, float inner, float outer)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            float c = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    t.SetPixel(x, y, d >= inner && d <= outer ? Color.white : Color.clear);
                }
            t.Apply();
            return t;
        }

        public static void OnGUI()
        {
            if (!InWorld) return;
            var cam = Camera.main;
            if (cam == null) return;
            EnsureAssets();
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            var old = GUI.color;

            // Partner cursor
            if (_partnerCursorValid && Time.realtimeSinceStartup - _partnerCursorTime < 2f && ToScreen(cam, _partnerCursor, out var sp))
            {
                GUI.color = PartnerColor;
                float s = 10f * scale;
                GUI.DrawTexture(new Rect(sp.x - s / 2, sp.y - s / 2, s, s), _dot);
                GUI.Label(new Rect(sp.x + s, sp.y - 8 * scale, 240 * scale, 22 * scale), CoopSession.PartnerName ?? "Partner", _label);
            }

            // Partner hooks
            GUI.color = PartnerColor;
            foreach (var id in PartnerHooks)
            {
                Transform t = null;
                try { t = TrackManager.GetTrackTransform(id); } catch { }
                if (t == null || !ToScreen(cam, t.position, out var hp)) continue;
                float s = 30f * scale;
                GUI.DrawTexture(new Rect(hp.x - s / 2, hp.y - s / 2, s, s), _ring);
            }

            // Pings
            float now = Time.realtimeSinceStartup;
            Pings.RemoveAll(p => p.until < now);
            foreach (var p in Pings)
            {
                if (!ToScreen(cam, p.pos, out var pp)) continue;
                float age = PingSeconds - (p.until - now);
                float pulse = Mathf.Repeat(age, 1f);
                float s = Mathf.Lerp(20f, 70f, pulse) * scale;
                GUI.color = new Color(p.mine ? 0.4f : PartnerColor.r, p.mine ? 1f : PartnerColor.g, p.mine ? 0.4f : PartnerColor.b, 1f - pulse);
                GUI.DrawTexture(new Rect(pp.x - s / 2, pp.y - s / 2, s, s), _ring);
            }
            GUI.color = old;
        }

        private static bool ToScreen(Camera cam, Vector2 world, out Vector2 gui)
        {
            Vector3 s = cam.WorldToScreenPoint(new Vector3(world.x, world.y, 0f));
            gui = new Vector2(s.x, Screen.height - s.y);
            return s.z >= 0f && s.x >= -50 && s.x <= Screen.width + 50 && s.y >= -50 && s.y <= Screen.height + 50;
        }
    }
}
