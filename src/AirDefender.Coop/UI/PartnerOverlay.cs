using System.Collections.Generic;
using AirDefender;
using AirDefenderCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AirDefenderCoop.Ui
{
    /// <summary>
    /// Shows where the other players are looking: their cursors on the map, the tracks they have
    /// hooked, and "pings" (middle mouse button) that pulse on every screen. Each player has a
    /// colour by slot. Clients send to the host, which forwards to everyone else.
    /// </summary>
    public static class PartnerOverlay
    {
        private const float SendInterval = 0.2f;
        private const float PingSeconds = 6f;

        private sealed class Remote
        {
            public Vector2 Cursor;
            public bool CursorValid;
            public float CursorTime;
            public readonly List<string> Hooks = new List<string>();
        }

        private static float _nextSend;
        private static readonly Dictionary<byte, Remote> Remotes = new Dictionary<byte, Remote>();
        private static readonly List<(Vector2 pos, float until, byte slot)> Pings = new List<(Vector2, float, byte)>();
        private static readonly List<byte> Gone = new List<byte>();
        private static Texture2D _ring, _dot;
        private static GUIStyle _label;

        // By slot: host orange, then cyan, magenta, yellow, ... Your own pings are green.
        private static readonly Color[] SlotColors =
        {
            new Color(1f, 0.55f, 0.1f, 0.95f),
            new Color(0.25f, 0.85f, 1f, 0.95f),
            new Color(1f, 0.35f, 0.85f, 0.95f),
            new Color(1f, 0.95f, 0.25f, 0.95f),
            new Color(0.65f, 0.55f, 1f, 0.95f),
            new Color(1f, 0.4f, 0.4f, 0.95f),
            new Color(0.6f, 1f, 0.85f, 0.95f),
            new Color(0.95f, 0.75f, 0.55f, 0.95f),
        };
        private static readonly Color MineColor = new Color(0.4f, 1f, 0.4f, 1f);

        public static Color ColorOf(byte slot) => SlotColors[slot % SlotColors.Length];

        public static void Init()
        {
            CoopSession.RegisterRelayed(MsgType.PartnerCursor, OnCursor, reliable: false);
            CoopSession.RegisterRelayed(MsgType.PingMarker, OnPing, reliable: true);
            CoopSession.PartnerLeft += _ => { Remotes.Clear(); Pings.Clear(); };
            CoopSession.Stopped += _ => { Remotes.Clear(); Pings.Clear(); };
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
                Pings.Add((p, Time.realtimeSinceStartup + PingSeconds, CoopSession.LocalSlot));
                CoopSession.Send(MsgType.PingMarker, w => { w.U8(CoopSession.LocalSlot); w.Vec2(p); });
                try { SfxService.PlaySelectTick(); } catch { }
            }

            float now = Time.realtimeSinceStartup;
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            DropDeparted();
            List<string> hooks = null;
            try { hooks = HookManager.GetAll(); } catch { }
            CoopSession.Send(MsgType.PartnerCursor, w =>
            {
                w.U8(CoopSession.LocalSlot);
                w.Bool(inside);
                w.Vec2(new Vector2(world.x, world.y));
                int n = hooks == null ? 0 : Mathf.Min(hooks.Count, 8);
                w.U8((byte)n);
                for (int i = 0; i < n; i++) w.Str(hooks[i]);
            }, reliable: false);
        }

        /// <summary>Forget players who are no longer in the session.</summary>
        private static void DropDeparted()
        {
            Gone.Clear();
            foreach (var slot in Remotes.Keys)
            {
                bool present = false;
                foreach (var p in CoopSession.Players) if (p.Slot == slot) { present = true; break; }
                if (!present) Gone.Add(slot);
            }
            foreach (var slot in Gone) Remotes.Remove(slot);
        }

        private static void OnCursor(NetReader r)
        {
            byte slot = r.U8();
            if (slot == CoopSession.LocalSlot) return;
            if (!Remotes.TryGetValue(slot, out var rem)) { rem = new Remote(); Remotes[slot] = rem; }
            rem.CursorValid = r.Bool();
            rem.Cursor = r.Vec2();
            rem.CursorTime = Time.realtimeSinceStartup;
            rem.Hooks.Clear();
            int n = r.U8();
            for (int i = 0; i < n; i++) rem.Hooks.Add(r.Str());
        }

        private static void OnPing(NetReader r)
        {
            byte slot = r.U8();
            var p = r.Vec2();
            if (slot == CoopSession.LocalSlot) return;
            Pings.Add((p, Time.realtimeSinceStartup + PingSeconds, slot));
            try { SfxService.PlayMessageBeep(); } catch { }
        }

        private static void EnsureAssets()
        {
            if (_ring != null) return;
            _ring = MakeCircle(64, 0.82f, 1f);
            _dot = MakeCircle(16, 0f, 1f);
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
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

            foreach (var kv in Remotes)
            {
                var rem = kv.Value;
                Color color = ColorOf(kv.Key);

                // Cursor with the player's callsign
                if (rem.CursorValid && Time.realtimeSinceStartup - rem.CursorTime < 2f && ToScreen(cam, rem.Cursor, out var sp))
                {
                    GUI.color = color;
                    _label.normal.textColor = color;
                    float s = 10f * scale;
                    GUI.DrawTexture(new Rect(sp.x - s / 2, sp.y - s / 2, s, s), _dot);
                    GUI.Label(new Rect(sp.x + s, sp.y - 8 * scale, 240 * scale, 22 * scale), CoopSession.NameOfSlot(kv.Key), _label);
                }

                // Hooked tracks
                GUI.color = color;
                foreach (var id in rem.Hooks)
                {
                    Transform t = null;
                    try { t = TrackManager.GetTrackTransform(id); } catch { }
                    if (t == null || !ToScreen(cam, t.position, out var hp)) continue;
                    float s = 30f * scale;
                    GUI.DrawTexture(new Rect(hp.x - s / 2, hp.y - s / 2, s, s), _ring);
                }
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
                Color c = p.slot == CoopSession.LocalSlot ? MineColor : ColorOf(p.slot);
                GUI.color = new Color(c.r, c.g, c.b, 1f - pulse);
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
