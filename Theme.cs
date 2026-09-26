using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Apocasetter
{
    // =====================================================================
    //  Theme: skins the IMGUI window from  <plugin dir>/theme/theme.json
    //
    //  Every texture slot accepts:
    //     "file.png"          -> PNG in the theme folder (9-sliced with "border")
    //     "game:SpriteName"   -> a Sprite the game has loaded (border taken from the sprite)
    //     "game:tex:Name"     -> a raw Texture2D the game has loaded
    //     "#RRGGBBAA"         -> flat colour
    //     ""                  -> keep Unity's default
    //  "font" accepts  "os:Segoe UI"  |  "game:FontName"  |  ""
    // =====================================================================
    [Serializable]
    public class ThemeSpec
    {
        public string font = "";
        public int fontSize = 14;
        public string textColor = "#E8DCC0FF";
        public string textColorHover = "#FFFFFFFF";
        public string headerTextColor = "#E8DCC0FF";

        public Slice window = new Slice();
        public Slice leftPanel = new Slice();
        public Slice rightPanel = new Slice();
        public Slice button = new Slice();
        public Slice buttonHover = new Slice();
        public Slice buttonActive = new Slice();
        public Slice category = new Slice();
        public Slice categoryHover = new Slice();
        public Slice categorySelected = new Slice();
        public Slice textField = new Slice();
        public Slice toggle = new Slice();
        public Slice toggleOn = new Slice();
        public Slice scrollbar = new Slice();
        public Slice scrollbarThumb = new Slice();

        public int[] windowPadding = { 12, 12, 28, 12 };   // left, right, top, bottom
        public int buttonHeight = 26;
        public int categoryHeight = 26;
    }

    [Serializable]
    public class Slice
    {
        public string texture = "";
        public int[] border = null;   // left, right, top, bottom ; null = use sprite border or 0
    }

    public static class Theme
    {
        public static GUISkin Skin;               // null until first OnGUI
        public static GUIStyle Category, CategorySelectedDummy, LeftPanel, RightPanel, Header, SubLabel;
        public static ThemeSpec Spec = new ThemeSpec();
        public static string ThemeDir;
        private static bool _built;
        private static readonly List<UnityEngine.Object> _keepAlive = new List<UnityEngine.Object>();

        public static void Load(string pluginDllPath)
        {
            ThemeDir = Path.Combine(Path.GetDirectoryName(pluginDllPath) ?? ".", "theme");
            var json = Path.Combine(ThemeDir, "theme.json");
            if (File.Exists(json))
            {
                try { Spec = ParseSpec(File.ReadAllText(json)); Plugin.Log.LogInfo($"Theme loaded from {json}"); }
                catch (Exception e) { Plugin.Log.LogWarning($"theme.json unreadable, using defaults: {e.Message}"); Spec = new ThemeSpec(); }
            }
            else Plugin.Log.LogInfo($"No theme.json at {json}; using Unity default skin");
            _built = false;
        }

        private static Slice S(Dictionary<string, object> d, string key)
        {
            object v; d.TryGetValue(key, out v);
            var o = MiniJson.Obj(v);
            if (o == null) return new Slice();
            var b = MiniJson.IntArray(o, "border");
            return new Slice { texture = MiniJson.Str(o, "texture", ""), border = b != null && b.Length == 4 ? b : null };
        }

        private static ThemeSpec ParseSpec(string text)
        {
            var d = MiniJson.Obj(MiniJson.Parse(text)) ?? new Dictionary<string, object>();
            var t = new ThemeSpec();
            t.font = MiniJson.Str(d, "font", t.font);
            t.fontSize = MiniJson.Int(d, "fontSize", t.fontSize);
            t.textColor = MiniJson.Str(d, "textColor", t.textColor);
            t.textColorHover = MiniJson.Str(d, "textColorHover", t.textColorHover);
            t.headerTextColor = MiniJson.Str(d, "headerTextColor", t.headerTextColor);
            t.buttonHeight = MiniJson.Int(d, "buttonHeight", t.buttonHeight);
            t.categoryHeight = MiniJson.Int(d, "categoryHeight", t.categoryHeight);
            var pad = MiniJson.IntArray(d, "windowPadding"); if (pad != null && pad.Length == 4) t.windowPadding = pad;

            t.window = S(d, "window"); t.leftPanel = S(d, "leftPanel"); t.rightPanel = S(d, "rightPanel");
            t.button = S(d, "button"); t.buttonHover = S(d, "buttonHover"); t.buttonActive = S(d, "buttonActive");
            t.category = S(d, "category"); t.categoryHover = S(d, "categoryHover"); t.categorySelected = S(d, "categorySelected");
            t.textField = S(d, "textField"); t.toggle = S(d, "toggle"); t.toggleOn = S(d, "toggleOn");
            t.scrollbar = S(d, "scrollbar"); t.scrollbarThumb = S(d, "scrollbarThumb");
            Plugin.Log.LogInfo($"theme: window='{t.window.texture}' button='{t.button.texture}' category='{t.category.texture}' font='{t.font}'");
            return t;
        }

        /// Call from OnGUI. Builds the skin once (needs GUI context) and applies it.
        public static void Apply()
        {
            if (!_built) Build();
            if (Skin != null) GUI.skin = Skin;
        }

        private static void Build()
        {
            _built = true;
            try
            {
                Skin = UnityEngine.Object.Instantiate(GUI.skin);
                _keepAlive.Add(Skin);

                var font = LoadFont(Spec.font, Spec.fontSize);
                if (font != null) Skin.font = font;
                var text = ParseColor(Spec.textColor, Color.white);
                var textHover = ParseColor(Spec.textColorHover, Color.white);

                foreach (var st in new[] { Skin.label, Skin.button, Skin.toggle, Skin.textField, Skin.window, Skin.box })
                {
                    st.fontSize = Spec.fontSize;
                    SetTextColor(st, text, textHover);
                }

                ApplySlice(Skin.window, Spec.window, "normal", "onNormal");
                Skin.window.padding = new RectOffset(Spec.windowPadding[0], Spec.windowPadding[1], Spec.windowPadding[2], Spec.windowPadding[3]);
                Skin.window.alignment = TextAnchor.UpperCenter;
                SetTextColor(Skin.window, ParseColor(Spec.headerTextColor, text), ParseColor(Spec.headerTextColor, text));

                ApplySlice(Skin.button, Spec.button, "normal");
                ApplySlice(Skin.button, Spec.buttonHover, "hover");
                ApplySlice(Skin.button, Spec.buttonActive, "active");
                Skin.button.fixedHeight = Spec.buttonHeight;

                ApplySlice(Skin.textField, Spec.textField, "normal", "hover", "focused");
                ApplySlice(Skin.toggle, Spec.toggle, "normal", "hover");
                ApplySlice(Skin.toggle, Spec.toggleOn, "onNormal", "onHover");
                ApplySlice(Skin.verticalScrollbar, Spec.scrollbar, "normal");
                ApplySlice(Skin.verticalScrollbarThumb, Spec.scrollbarThumb, "normal", "hover", "active");

                LeftPanel = new GUIStyle(Skin.box); ApplySlice(LeftPanel, Spec.leftPanel, "normal");
                RightPanel = new GUIStyle(Skin.box); ApplySlice(RightPanel, Spec.rightPanel, "normal");

                Category = new GUIStyle(Skin.button) { fixedHeight = Spec.categoryHeight, alignment = TextAnchor.MiddleLeft };
                ApplySlice(Category, Spec.category, "normal");
                ApplySlice(Category, Spec.categoryHover, "hover");
                ApplySlice(Category, Spec.categorySelected, "onNormal", "onHover", "onActive", "active");

                Header = new GUIStyle(Skin.label) { fontStyle = FontStyle.Bold };
                var header = ParseColor(Spec.headerTextColor, text);
                SetTextColor(Header, header, textHover);
                SubLabel = new GUIStyle(Skin.label) { fontSize = Math.Max(9, Spec.fontSize - 2) };
                SetTextColor(SubLabel, new Color(text.r, text.g, text.b, text.a * 0.7f), textHover);

                Plugin.Log.LogInfo("Theme skin built");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Theme build failed, falling back to default skin: " + e);
                Skin = null;
            }
        }

        private static void SetTextColor(GUIStyle st, Color normal, Color hover)
        {
            st.normal.textColor = normal; st.onNormal.textColor = normal;
            st.hover.textColor = hover; st.onHover.textColor = hover;
            st.active.textColor = hover; st.onActive.textColor = hover;
            st.focused.textColor = hover; st.onFocused.textColor = hover;
        }

        private static void ApplySlice(GUIStyle st, Slice slice, params string[] states)
        {
            if (slice == null || string.IsNullOrEmpty(slice.texture)) return;
            var tex = LoadTexture(slice.texture, out var spriteBorder);
            if (tex == null) return;
            var b = slice.border != null && slice.border.Length == 4 ? new RectOffset(slice.border[0], slice.border[1], slice.border[2], slice.border[3])
                  : spriteBorder ?? new RectOffset(0, 0, 0, 0);
            st.border = b;
            foreach (var s in states)
            {
                var gs = StateOf(st, s);
                if (gs != null) gs.background = tex;
            }
        }

        private static GUIStyleState StateOf(GUIStyle st, string name)
        {
            switch (name)
            {
                case "normal": return st.normal; case "hover": return st.hover; case "active": return st.active; case "focused": return st.focused;
                case "onNormal": return st.onNormal; case "onHover": return st.onHover; case "onActive": return st.onActive; case "onFocused": return st.onFocused;
            }
            return null;
        }

        // ---------------------------------------------------------------- assets
        private static readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();

        public static Texture2D LoadTexture(string spec, out RectOffset spriteBorder)
        {
            spriteBorder = null;
            if (string.IsNullOrEmpty(spec)) return null;
            if (_texCache.TryGetValue(spec, out var cached)) return cached;
            Texture2D result = null;
            try
            {
                if (spec.StartsWith("#"))
                {
                    result = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    var c = ParseColor(spec, Color.magenta);
                    result.SetPixels(Enumerable.Repeat(c, 16).ToArray()); result.Apply();
                }
                else if (spec.StartsWith("game:tex:"))
                {
                    var n = spec.Substring(9);
                    result = Resources.FindObjectsOfTypeAll<Texture2D>().FirstOrDefault(t => t.name == n);
                    if (result == null) Plugin.Log.LogWarning($"theme: game texture '{n}' not found");
                }
                else if (spec.StartsWith("game:"))
                {
                    var n = spec.Substring(5);
                    var sprite = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == n);
                    if (sprite == null) Plugin.Log.LogWarning($"theme: game sprite '{n}' not found");
                    else
                    {
                        result = ExtractSprite(sprite);
                        var sb = sprite.border; // x=left, y=bottom, z=right, w=top
                        spriteBorder = new RectOffset((int)sb.x, (int)sb.z, (int)sb.w, (int)sb.y);
                    }
                }
                else
                {
                    var path = Path.Combine(ThemeDir, spec);
                    if (!File.Exists(path)) Plugin.Log.LogWarning($"theme: file not found {path}");
                    else
                    {
                        result = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        var bytes = File.ReadAllBytes(path);
                        if (!ImageConversion.LoadImage(result, bytes, false)) { Plugin.Log.LogWarning($"theme: could not decode {path}"); result = null; }
                        else
                        {
                            result.wrapMode = TextureWrapMode.Clamp; result.filterMode = FilterMode.Bilinear;
                            result.Apply(false, false);
                            result.name = "theme:" + spec;
                            Plugin.Log.LogInfo($"theme: loaded {spec} {result.width}x{result.height} {result.format} ({bytes.Length} bytes)");
                        }
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"theme: {spec}: {e.Message}"); }
            if (result != null) { result.hideFlags = HideFlags.HideAndDontSave; _keepAlive.Add(result); }
            _texCache[spec] = result;
            return result;
        }

        // Sprites usually live in atlases; copy the sprite's rect out on the GPU so IMGUI can draw it alone.
        private static Texture2D ExtractSprite(Sprite s)
        {
            var src = s.texture;
            var r = s.textureRect;
            if ((int)r.width == src.width && (int)r.height == src.height) return src;
            try
            {
                var dst = new Texture2D((int)r.width, (int)r.height, src.format, false);
                Graphics.CopyTexture(src, 0, 0, (int)r.x, (int)r.y, (int)r.width, (int)r.height, dst, 0, 0, 0, 0);
                return dst;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"theme: cannot extract sprite '{s.name}' from its atlas ({e.Message}); using whole atlas");
                return src;
            }
        }

        private static Font LoadFont(string spec, int size)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            try
            {
                if (spec.StartsWith("os:")) return Font.CreateDynamicFontFromOSFont(spec.Substring(3), size);
                if (spec.StartsWith("game:"))
                {
                    var n = spec.Substring(5);
                    var f = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(x => x.name == n);
                    if (f == null) Plugin.Log.LogWarning($"theme: game font '{n}' not found");
                    return f;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"theme font '{spec}': {e.Message}"); }
            return null;
        }

        public static bool Debug;
        /// Draws every cached texture raw, to check they decoded and uploaded correctly.
        public static void DrawDebugStrip()
        {
            GUILayout.BeginHorizontal();
            foreach (var kv in _texCache)
            {
                var t = kv.Value;
                GUILayout.BeginVertical(GUILayout.Width(70));
                var r = GUILayoutUtility.GetRect(64, 64);
                if (t != null) GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true);
                GUILayout.Label(t != null ? $"{kv.Key}\n{t.width}x{t.height}" : kv.Key + "\nNULL", GUI.skin.label);
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            var b = Skin?.button?.normal?.background;
            GUILayout.Label($"skin.button.normal.background = {(b != null ? b.name : "null")}   GUI.skin == Skin: {ReferenceEquals(GUI.skin, Skin)}");
        }

        public static Color ParseColor(string hex, Color fallback)
            => !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;

        /// Writes BepInEx/ui_assets.txt listing the game's fonts and UI-looking sprites/textures so you can pick "game:" names.
        public static void DumpGameUiAssets()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== Fonts (use as  \"font\": \"game:<name>\")");
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>().OrderBy(f => f.name)) sb.AppendLine("   " + f.name);
            sb.AppendLine();
            sb.AppendLine("== TextMeshPro font assets (NOT usable by IMGUI directly; listed so you know what the game uses; their source font may appear above)");
            try
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>().OrderBy(f => f.name))
                    sb.AppendLine($"   {t.name}   source={(t.sourceFontFile != null ? t.sourceFontFile.name : "-")}");
            }
            catch (Exception e) { sb.AppendLine("   (TMP not available: " + e.Message + ")"); }
            sb.AppendLine();
            sb.AppendLine("== Sprites (use as  \"texture\": \"game:<name>\")   name  size  border(l,r,t,b)  atlas");
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>().OrderBy(s => s.name))
            {
                var b = s.border;
                sb.AppendLine($"   {s.name,-40} {(int)s.rect.width}x{(int)s.rect.height}  border=({b.x},{b.z},{b.w},{b.y})  tex={s.texture?.name}");
            }
            sb.AppendLine();
            sb.AppendLine("== Textures with UI-ish names (use as  \"texture\": \"game:tex:<name>\")");
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>().Where(t => LooksUi(t.name)).OrderBy(t => t.name))
                sb.AppendLine($"   {t.name,-40} {t.width}x{t.height}");
            var path = Path.Combine(BepInEx.Paths.BepInExRootPath, "ui_assets.txt");
            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogInfo($"UI asset list written to {path}");
        }

        private static bool LooksUi(string n)
        {
            n = n.ToLowerInvariant();
            return n.Contains("ui") || n.Contains("button") || n.Contains("panel") || n.Contains("frame") || n.Contains("bg") || n.Contains("background")
                || n.Contains("menu") || n.Contains("border") || n.Contains("box") || n.Contains("slot") || n.Contains("window") || n.Contains("paper");
        }
    }
}
