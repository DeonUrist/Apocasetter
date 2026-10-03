using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Apocasetter
{
    // =====================================================================
    //  The Mods window's look: the main menu's rust plate, rebar frame and
    //  wooden planks (theme\game\*), the game's own menu font when it can be
    //  picked up from the menu buttons, white text with a black outline.
    //  Everything is sized by S (1 = a 1280x720 screen) so text stays sharp.
    //  The older Theme class stays as it is for Apocaspawner / Apocasaver.
    // =====================================================================
    public static class GameSkin
    {
        public static string Dir;
        public static Font GameFont;           // picked up from the cloned MODS button, if it uses a legacy Font
        public static float S { get; private set; }

        public static readonly Color Yellow = Hex("F2DC1E"), White = Hex("F4F0E8"), Desc = Hex("D2C8BA"), Sub = Hex("A79D90"),
            Dim = Hex("7E766B"), Red = Hex("FF6B57"), Green = Hex("CFE39A"), Black = Color.black, Line = Hex("2B241E"), Pit = new Color(0.035f, 0.027f, 0.024f, 0.9f);

        public static Texture2D Rust, Chains, RebarH, RebarV, Plank, PlankHover, PlankDown, Knob, Rivet, Slot, Hazard, Px, Track, Thumb, Field;
        public static GUIStyle Title, H1, Btn, BtnSmall, Section, Tab, Body, BodyBold, Small, Tiny, ListName, ListSub, TextField, PlankStyle,
            Invisible, ScrollV, ScrollThumb, Mono;

        private static Font _display, _body, _bold;
        private static bool _loaded;
        private static float _builtFor = -1;
        private static readonly List<UnityEngine.Object> _keep = new List<UnityEngine.Object>();

        public static void Init(string pluginDll)
        {
            Dir = Path.Combine(Path.Combine(Path.GetDirectoryName(pluginDll) ?? ".", "theme"), "game");
        }

        /// Call at the top of OnGUI.
        public static void Ensure(float s)
        {
            if (!_loaded) Load();
            if (Mathf.Abs(s - _builtFor) > 0.001f) Build(s);
        }

        private static void Load()
        {
            _loaded = true;
            Rust = Tex("rust.jpg"); Chains = Tex("chains.jpg"); Knob = Tex("knob.png"); Rivet = Tex("rivet.png");
            RebarH = Tex("rebar_h.png", true); RebarV = Tex("rebar_v.png", true);
            Plank = Tex("plank.png");
            PlankHover = Tint(Plank, 1.3f); PlankDown = Tint(Plank, 0.75f);
            Px = Solid(Color.white);
            Slot = Boxed(16, Hex("8F8A83"), Hex("060504"), 2);
            Field = Boxed(16, Hex("5C544B"), Hex("050403"), 2);
            Track = Boxed(8, Hex("120E0C"), Hex("120E0C"), 0);
            Thumb = Boxed(8, Hex("6A6158"), Hex("8F8A83"), 1);
            Hazard = Stripes();
            try
            {
                _body = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Segoe UI Symbol", "Arial" }, 14);
                _bold = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI Semibold", "Segoe UI", "Segoe UI Symbol", "Arial" }, 14);
            }
            catch (Exception e) { Plugin.Log.LogWarning("OS fonts: " + e.Message); }
            _display = GameFont;
            if (_display == null)
            {
                try { _display = Font.CreateDynamicFontFromOSFont(new[] { "Impact", "Arial Black", "Segoe UI Symbol", "Arial" }, 16); } catch { }
                Plugin.Log.LogInfo("Mods window: game menu font not found yet, using Impact");
            }
            else Plugin.Log.LogInfo("Mods window: using the game's menu font '" + _display.name + "'");
            foreach (var f in new[] { _body, _bold, _display }) if (f != null) _keep.Add(f);
        }

        /// The game font may only be found after the menu exists; rebuild the styles then.
        public static void SetGameFont(Font f)
        {
            if (f == null || f == GameFont) return;
            GameFont = f;
            if (_loaded) { _display = f; _builtFor = -1; Plugin.Log.LogInfo("Mods window: switched to the game's menu font '" + f.name + "'"); }
        }

        private static int F(float px) { return Mathf.Max(8, Mathf.RoundToInt(px * S)); }

        private static GUIStyle Text(Font f, float size, TextAnchor a = TextAnchor.MiddleLeft, bool wrap = false)
        {
            var st = new GUIStyle(GUIStyle.none) { font = f, fontSize = F(size), alignment = a, wordWrap = wrap, richText = false, clipping = TextClipping.Clip };
            st.normal.textColor = Color.white; st.hover.textColor = Color.white; st.active.textColor = Color.white;
            return st;
        }

        private static void Build(float s)
        {
            S = s; _builtFor = s;
            Title = Text(_display, 44); Title.clipping = TextClipping.Overflow;
            H1 = Text(_display, 30);
            Btn = Text(_display, 15, TextAnchor.MiddleCenter); Btn.clipping = TextClipping.Overflow;
            BtnSmall = Text(_display, 13, TextAnchor.MiddleCenter); BtnSmall.clipping = TextClipping.Overflow;
            Section = Text(_display, 17);
            Tab = Text(_display, 18, TextAnchor.MiddleCenter);
            Body = Text(_body, 14.5f, TextAnchor.UpperLeft, true);
            BodyBold = Text(_bold, 16);
            Small = Text(_body, 12.5f, TextAnchor.MiddleLeft, true);
            Tiny = Text(_bold, 11, TextAnchor.MiddleCenter);
            ListName = Text(_bold, 15);
            ListSub = Text(_body, 12);
            Mono = Text(_body, 11.5f);

            PlankStyle = new GUIStyle(GUIStyle.none) { border = new RectOffset(16, 16, 12, 12) };
            PlankStyle.normal.background = Plank; PlankStyle.hover.background = PlankHover; PlankStyle.active.background = PlankDown;
            PlankStyle.onNormal.background = Plank; PlankStyle.onHover.background = PlankHover;

            TextField = new GUIStyle(GUI.skin.textField) { font = _body, fontSize = F(14), alignment = TextAnchor.MiddleLeft, border = new RectOffset(3, 3, 3, 3), padding = new RectOffset(F(8), F(8), 2, 2) };
            foreach (var st in new[] { TextField.normal, TextField.hover, TextField.focused, TextField.active, TextField.onNormal, TextField.onFocused })
            { st.background = Field; st.textColor = White; }
            TextField.focused.background = Slot;

            Invisible = new GUIStyle(GUIStyle.none);
            ScrollV = new GUIStyle(GUI.skin.verticalScrollbar) { fixedWidth = F(8), border = new RectOffset(2, 2, 2, 2), margin = new RectOffset(F(4), 0, 0, 0) };
            ScrollV.normal.background = Track;
            ScrollThumb = new GUIStyle(GUI.skin.verticalScrollbarThumb) { fixedWidth = F(8), border = new RectOffset(2, 2, 2, 2) };
            ScrollThumb.normal.background = Thumb; ScrollThumb.hover.background = Thumb; ScrollThumb.active.background = Thumb;
        }

        // ---------------------------------------------------------------- drawing helpers
        public static void Fill(Rect r, Color c)
        {
            var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, Px); GUI.color = old;
        }

        /// White-on-black outlined text, the way the game draws its menu labels.
        public static void Out(Rect r, string text, GUIStyle st, Color c, float t = 1f)
        {
            var old = GUI.contentColor;
            GUI.contentColor = Black;
            float o = Mathf.Max(1f, t * S);
            GUI.Label(new Rect(r.x - o, r.y, r.width, r.height), text, st);
            GUI.Label(new Rect(r.x + o, r.y, r.width, r.height), text, st);
            GUI.Label(new Rect(r.x, r.y - o, r.width, r.height), text, st);
            GUI.Label(new Rect(r.x, r.y + o, r.width, r.height), text, st);
            GUI.Label(new Rect(r.x + o, r.y + o * 1.6f, r.width, r.height), text, st);
            GUI.contentColor = c;
            GUI.Label(r, text, st);
            GUI.contentColor = old;
        }

        public static void Label(Rect r, string text, GUIStyle st, Color c)
        {
            var old = GUI.contentColor; GUI.contentColor = c; GUI.Label(r, text, st); GUI.contentColor = old;
        }

        /// A wooden plank button with an outlined label. Returns true when clicked.
        public static bool PlankButton(Rect r, string text, Color c, GUIStyle font = null, bool enabled = true, float alpha = 1f)
        {
            var oldEnabled = GUI.enabled; var oldColor = GUI.color;
            GUI.enabled = enabled && oldEnabled;
            if (!GUI.enabled || alpha < 1f) GUI.color = new Color(1, 1, 1, enabled ? alpha : 0.45f);
            bool hit = GUI.Button(r, GUIContent.none, PlankStyle);
            Out(r, text, font ?? Btn, c);
            GUI.enabled = oldEnabled; GUI.color = oldColor;
            return hit;
        }

        public static void Frame(Rect r, float thick)
        {
            if (RebarH == null || RebarV == null) { Fill(new Rect(r.x, r.y, r.width, thick), Hex("3A3634")); Fill(new Rect(r.x, r.yMax - thick, r.width, thick), Hex("3A3634")); Fill(new Rect(r.x, r.y, thick, r.height), Hex("3A3634")); Fill(new Rect(r.xMax - thick, r.y, thick, r.height), Hex("3A3634")); return; }
            float hRepeat = r.width / (RebarH.width * thick / RebarH.height);
            float vRepeat = r.height / (RebarV.height * thick / RebarV.width);
            GUI.DrawTextureWithTexCoords(new Rect(r.x, r.y + thick * 0.1f, r.width, thick), RebarH, new Rect(0, 0, hRepeat, 1));
            GUI.DrawTextureWithTexCoords(new Rect(r.x, r.yMax - thick * 1.1f, r.width, thick), RebarH, new Rect(0, 0, hRepeat, 1));
            GUI.DrawTextureWithTexCoords(new Rect(r.x + thick * 0.1f, r.y, thick, r.height), RebarV, new Rect(0, 0, 1, vRepeat));
            GUI.DrawTextureWithTexCoords(new Rect(r.xMax - thick * 1.1f, r.y, thick, r.height), RebarV, new Rect(0, 0, 1, vRepeat));
        }

        public static void RustBack(Rect r)
        {
            Fill(r, Hex("141110"));
            if (Rust != null) GUI.DrawTexture(r, Rust, ScaleMode.ScaleAndCrop);
        }

        public static void PitBox(Rect r)
        {
            Fill(r, Pit);
            Fill(new Rect(r.x, r.y, r.width, 2), Hex("040302")); Fill(new Rect(r.x, r.yMax - 2, r.width, 2), Hex("040302"));
            Fill(new Rect(r.x, r.y, 2, r.height), Hex("040302")); Fill(new Rect(r.xMax - 2, r.y, 2, r.height), Hex("040302"));
            if (Rivet != null)
            {
                float k = 12 * S, m = 8 * S;
                GUI.DrawTexture(new Rect(r.x + m, r.y + m, k, k), Rivet);
                GUI.DrawTexture(new Rect(r.xMax - m - k, r.y + m, k, k), Rivet);
                GUI.DrawTexture(new Rect(r.x + m, r.yMax - m - k, k, k), Rivet);
                GUI.DrawTexture(new Rect(r.xMax - m - k, r.yMax - m - k, k, k), Rivet);
            }
        }

        public static void SlotBox(Rect r)
        {
            Fill(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), Hex("2A1D14"));
            GUI.Box(r, GUIContent.none, SlotStyle);
        }

        private static GUIStyle _slotStyle;
        private static GUIStyle SlotStyle
        {
            get
            {
                if (_slotStyle == null) { _slotStyle = new GUIStyle(GUIStyle.none) { border = new RectOffset(3, 3, 3, 3) }; _slotStyle.normal.background = Slot; }
                return _slotStyle;
            }
        }

        public static void HazardStrip(Rect r)
        {
            if (Hazard == null) { Fill(r, Yellow); return; }
            float tile = 18 * S;
            GUI.DrawTextureWithTexCoords(r, Hazard, new Rect(0, 0, r.width / tile, r.height / tile));
        }

        // ---------------------------------------------------------------- textures
        private static Texture2D Tex(string file, bool repeat = false)
        {
            var path = Path.Combine(Dir, file);
            try
            {
                if (!File.Exists(path)) { Plugin.Log.LogWarning("Mods window: missing " + path); return null; }
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(t, File.ReadAllBytes(path), false)) { Plugin.Log.LogWarning("Mods window: cannot decode " + path); return null; }
                t.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;
                t.hideFlags = HideFlags.HideAndDontSave;
                _keep.Add(t);
                return t;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Mods window: " + file + ": " + e.Message); return null; }
        }

        private static Texture2D Tint(Texture2D src, float k)
        {
            if (src == null) return null;
            try
            {
                var px = src.GetPixels();
                for (int i = 0; i < px.Length; i++) px[i] = new Color(Mathf.Clamp01(px[i].r * k), Mathf.Clamp01(px[i].g * k), Mathf.Clamp01(px[i].b * k), px[i].a);
                var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                t.SetPixels(px); t.Apply();
                _keep.Add(t);
                return t;
            }
            catch { return src; }
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixels(new[] { c, c, c, c }); t.Apply(); _keep.Add(t);
            return t;
        }

        private static Texture2D Boxed(int n, Color border, Color inner, int bw)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                    px[y * n + x] = (x < bw || y < bw || x >= n - bw || y >= n - bw) ? border : inner;
            t.SetPixels(px); t.Apply(); _keep.Add(t);
            return t;
        }

        private static Texture2D Stripes()
        {
            const int n = 32;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            var px = new Color[n * n];
            Color a = Yellow, b = Hex("141210");
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) px[y * n + x] = ((x + y) % n) < n / 2 ? a : b;
            t.SetPixels(px); t.Apply(); _keep.Add(t);
            return t;
        }

        public static Color Hex(string h)
        {
            Color c;
            return ColorUtility.TryParseHtmlString("#" + h, out c) ? c : Color.magenta;
        }
    }
}
