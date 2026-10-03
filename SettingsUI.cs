using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.EventSystems;
using S = Apocasetter.GameSkin;

namespace Apocasetter
{
    // =====================================================================
    //  The Mods window (2.0): mod list with icons on the left, the selected
    //  mod on the right (settings / what's new / files), update banner,
    //  remove dialog. Drawn with IMGUI in the game's own look (GameSkin).
    //  Method names OnGUI and Draw are kept: ApocaLanguage hooks them to
    //  translate the window.
    // =====================================================================
    public class SettingsUI : MonoBehaviour
    {
        public static SettingsUI Instance;

        // window geometry in "virtual" units (1 = 1 px on a 1280x720 screen)
        private const float WW = 1220, WH = 688, FR = 20;
        private const float RailX = 36, RailW = 276, TopY = 96, PaneX = 328, PaneW = 856, BottomY = 616;
        private float _sc = 1, _wx, _wy;

        private bool _open;
        private string _selected = "";
        private string _filter = "all", _tab = "settings", _search = "", _settingSearch = "";
        private Vector2 _listScroll, _rightScroll, _dropScroll;
        private readonly Dictionary<ConfigEntryBase, string> _pending = new Dictionary<ConfigEntryBase, string>();
        private readonly Dictionary<ConfigEntryBase, string> _errors = new Dictionary<ConfigEntryBase, string>();
        private ConfigEntryBase _dropdown, _rebind, _sliderDrag;
        private readonly HashSet<string> _showAll = new HashSet<string>(), _showOther = new HashSet<string>();
        private bool _removeOpen, _removeCfg;
        private string _status = "";
        private float _statusUntil;
        private bool _toastDismissed;
        private Dictionary<string, List<string>> _keyUsers;

        // cursor bookkeeping (1.0.1: re-lock after Alt+Tab)
        private CursorLockMode _prevLock, _lastLock = CursorLockMode.None;
        private bool _prevVisible, _lastVisible = true, _unblockNextFrame;
        private int _relockFrames;

        private static readonly Dictionary<string, string> GameKeys = new Dictionary<string, string> { { "F2", "the game (Heal)" }, { "F9", "the game (hide UI)" } };

        private void Awake() { Instance = this; }

        private void OnDestroy() { UnmuteGameUi(); if (_open) InputBlocker.Set(false); }

        private void Start()
        {
            if (Plugin.CheckUpdatesEntry == null || Plugin.CheckUpdatesEntry.Value) StartCoroutine(Updates.Check(false));
            Updates.Changed += () => Catalog.Invalidate();
        }

        // ---------------------------------------------------------------- frame logic
        private void Update()
        {
            if (_unblockNextFrame) { _unblockNextFrame = false; if (!_open) InputBlocker.Set(false); }
            if (_open && _rebind != null) { PollRebind(); return; }
            if (Plugin.Pressed(Plugin.MenuKeyEntry.Value)) Toggle();
            else if (_open && Plugin.Pressed(Key.Escape))
            {
                if (_removeOpen) _removeOpen = false;
                else if (_dropdown != null) _dropdown = null;
                else Toggle();
            }
            if (!_open) GameMenu.Tick(Open);
        }

        // The game's own menus (UGUI) must not react while the window is open: clicks on the window would otherwise reach
        // the title menu below it (LOAD GAME, NEW GAME...). Every EventSystem is switched off and put back on close.
        private readonly List<EventSystem> _mutedEventSystems = new List<EventSystem>();

        private void MuteGameUi()
        {
            try
            {
                foreach (var es in FindObjectsOfType<EventSystem>())
                    if (es != null && es.enabled) { es.enabled = false; _mutedEventSystems.Add(es); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Could not switch off the game's UI input: " + e.Message); }
        }

        private void UnmuteGameUi()
        {
            foreach (var es in _mutedEventSystems) if (es != null) es.enabled = true;
            _mutedEventSystems.Clear();
        }

        private void LateUpdate()
        {
            if (_open)
            {
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                if (Time.frameCount % 15 == 0) MuteGameUi();   // an EventSystem created later (scene load) is caught too
                if (Time.timeScale != 0f && InputBlocker.Active) Time.timeScale = 0f;   // keep the game paused
                return;
            }
            if (_relockFrames > 0)
            {
                _relockFrames--;
                if (InputBlocker.Active) { _relockFrames = 0; return; }
                Cursor.lockState = CursorLockMode.None;
                Cursor.lockState = _lastLock;
                Cursor.visible = _lastVisible;
                if (_relockFrames == 0) Plugin.Log.LogInfo("Cursor re-locked after focus regain (" + _lastLock + ")");
                return;
            }
            if (Application.isFocused && !InputBlocker.Active) { _lastLock = Cursor.lockState; _lastVisible = Cursor.visible; }
        }

        private void OnApplicationFocus(bool focus)
        {
            if (!focus) { _relockFrames = 0; return; }
            if (!_open && !InputBlocker.Active && _lastLock != CursorLockMode.None && Plugin.RestoreCursorEntry != null && Plugin.RestoreCursorEntry.Value)
                _relockFrames = 3;
        }

        public void Open() { if (!_open) Toggle(); }

        private void Toggle()
        {
            _open = !_open;
            if (_open)
            {
                _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
                InputBlocker.Set(true);
                MuteGameUi();
                Catalog.Invalidate();
                _pending.Clear(); _errors.Clear(); _dropdown = null; _rebind = null; _removeOpen = false; _keyUsers = null;
                _toastDismissed = true;
            }
            else
            {
                if (_pending.Count > 0) ApplyPending();
                Cursor.lockState = _prevLock; Cursor.visible = _prevVisible;
                UnmuteGameUi();
                _unblockNextFrame = true; // keep blocking one more frame so the closing keypress isn't seen by the game
            }
        }

        private void PollRebind()
        {
            var kb = Keyboard.current;
            if (kb == null) { _rebind = null; return; }
            foreach (var k in kb.allKeys)
            {
                if (k == null || !k.wasPressedThisFrame) continue;
                var e = _rebind;
                _rebind = null;
                if (k.keyCode == Key.Escape) return;
                SetKey(e, (k.keyCode == Key.Backspace || k.keyCode == Key.Delete) ? Key.None : k.keyCode);
                return;
            }
        }

        // ---------------------------------------------------------------- OnGUI
        private void OnGUI()
        {
            if (!_open) { DrawMenuExtras(); return; }

            _sc = Mathf.Clamp(Mathf.Min(Screen.width / 1280f, Screen.height / 720f), 0.6f, 4f);
            S.Ensure(_sc);
            _wx = Mathf.Round((Screen.width - WW * _sc) / 2f);
            _wy = Mathf.Round((Screen.height - WH * _sc) / 2f);

            // slider released: write the value once instead of on every drag frame
            if (_sliderDrag != null && Event.current.rawType == EventType.MouseUp)
            {
                var e = _sliderDrag; _sliderDrag = null;
                string v;
                if (_pending.TryGetValue(e, out v)) SetSerialized(e, v);
            }
            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) && _pending.Count > 0)
            { ApplyPending(); Event.current.Use(); }

            S.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.55f));
            Draw(0);
        }

        /// Width (virtual units) a plank needs for its label. The label is measured as drawn, so a translation (ApocaLanguage) widens it too.
        private float BW(string text, float min, GUIStyle st = null, float pad = 36)
        {
            var w = (st ?? S.Btn).CalcSize(new GUIContent(text)).x / _sc + pad;
            return Mathf.Max(min, Mathf.Ceil(w));
        }

        private Rect R(float x, float y, float w, float h) { return new Rect(_wx + x * _sc, _wy + y * _sc, w * _sc, h * _sc); }
        private float U(float v) { return v * _sc; }

        private void Draw(int id)
        {
            var mods = Catalog.All();
            if (string.IsNullOrEmpty(_selected) || !mods.Any(m => m.Guid == _selected))
            {
                var first = mods.FirstOrDefault(m => Status(m).Update) ?? mods.FirstOrDefault();
                _selected = first != null ? first.Guid : "";
            }
            var mod = mods.FirstOrDefault(m => m.Guid == _selected);

            var win = R(0, 0, WW, WH);
            S.RustBack(win);
            S.Frame(win, U(FR));

            bool modal = _removeOpen;
            var oldEnabled = GUI.enabled;
            if (modal) GUI.enabled = false;
            DrawHeader(mods);
            DrawRail(mods);
            if (mod != null) DrawPane(mod);
            DrawBottom(mod);
            GUI.enabled = oldEnabled;
            if (modal && mod != null) DrawRemoveDialog(mod);
        }

        // ---------------------------------------------------------------- header
        private void DrawHeader(List<ModEntry> mods)
        {
            var titleR = R(FR + 18, FR + 4, 160, 62);
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(-3f, titleR.center);
            S.Out(titleR, "MODS", S.Title, S.Yellow, 1.5f);
            GUI.matrix = m;

            int updates = mods.Count(mm => Status(mm).Update);
            int plugins = mods.Count(mm => mm.Kind == ModKind.Loaded);
            string head = plugins + " PLUGINS";
            if (Updates.Checking) head += "  ·  CHECKING FOR UPDATES…";
            else if (Updates.HaveIndex) head += "  ·  " + updates + (updates == 1 ? " UPDATE" : " UPDATES") + " ON GITHUB";
            else if (Updates.CheckError != null) head += "  ·  UPDATE CHECK FAILED";
            S.Out(R(FR + 168, FR + 22, 520, 28), head, S.BodyBold, S.White);

            float x = WW - FR - 18;
            float cw = BW("CLOSE", 104); x -= cw;
            if (S.PlankButton(R(x, FR + 14, cw, 42), "CLOSE", S.White)) Toggle();
            float uw = BW("CHECK FOR UPDATES", 196); x -= uw + 8;
            if (S.PlankButton(R(x, FR + 14, uw, 42), "CHECK FOR UPDATES", S.White, null, !Updates.Checking)) StartCoroutine(Updates.Check(true));
            string when = Updates.Checking ? "Checking…" : Updates.CheckError != null ? "No connection to GitHub" : Updates.HaveIndex ? "Checked " + Ago(Updates.IndexTimeUtc) : "Not checked yet";
            S.Out(R(x - 230, FR + 22, 222, 28), when, S.Small, Updates.CheckError != null ? S.Red : S.Desc);
            S.Fill(R(FR, TopY - 6, WW - 2 * FR, 2), new Color(0, 0, 0, 0.55f));
        }

        private static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 48) return (int)d.TotalHours + " h ago";
            return (int)d.TotalDays + " days ago";
        }

        // ---------------------------------------------------------------- left rail
        private class Group { public string Title; public List<ModEntry> Items; }

        private void DrawRail(List<ModEntry> mods)
        {
            // search plank
            var sr = R(RailX, TopY + 4, RailW, 42);
            GUI.Box(sr, GUIContent.none, S.PlankStyle);
            var fieldR = new Rect(sr.x + U(14), sr.y + U(6), sr.width - U(28), sr.height - U(12));
            GUI.SetNextControlName("apocasetter-search");
            _search = GUI.TextField(fieldR, _search, 40, Transparent(S.BodyBold));
            if (_search.Length == 0 && GUI.GetNameOfFocusedControl() != "apocasetter-search") S.Out(fieldR, "SEARCH MODS", LeftBtn(), S.Desc);

            // filter planks
            int nUpd = mods.Count(m => IsUpdateListed(m)), nOther = mods.Count(m => m.Other || m.Kind == ModKind.Disabled);
            var chips = new[] { new KeyValuePair<string, string>("all", "ALL " + mods.Count(m => m.Kind != ModKind.Available)),
                                new KeyValuePair<string, string>("updates", "UPDATES " + nUpd),
                                new KeyValuePair<string, string>("other", "OTHER " + nOther) };
            var widths = chips.Select(c => BW(c.Value, 60, S.BtnSmall, 26)).ToArray();
            float total = widths.Sum() + 2 * (chips.Length - 1);
            if (total < RailW) { float extra = (RailW - total) / chips.Length; for (int i = 0; i < widths.Length; i++) widths[i] += extra; }
            else { float k = (RailW - 2 * (chips.Length - 1)) / widths.Sum(); for (int i = 0; i < widths.Length; i++) widths[i] *= k; }
            float cx = RailX;
            for (int i = 0; i < chips.Length; i++)
            {
                bool on = _filter == chips[i].Key;
                if (S.PlankButton(R(cx, TopY + 52, widths[i], 36), chips[i].Value, on ? S.Yellow : S.White, S.BtnSmall, true, on ? 1f : 0.62f)) { _filter = chips[i].Key; _listScroll = Vector2.zero; }
                cx += widths[i] + 2;
            }

            // groups
            var q = _search.Trim().ToLowerInvariant();
            Func<ModEntry, bool> match = m => q.Length == 0 || (m.Name ?? "").ToLowerInvariant().Contains(q);
            var groups = new List<Group>();
            if (_filter == "updates")
            {
                groups.Add(new Group { Title = "UPDATES", Items = mods.Where(m => m.Kind == ModKind.Loaded && IsUpdateListed(m)).Where(match).ToList() });
                groups.Add(new Group { Title = "ON GITHUB", Items = mods.Where(m => m.Kind == ModKind.Available).Where(match).ToList() });
            }
            else if (_filter == "other")
            {
                groups.Add(new Group { Title = "OTHER PLUGINS", Items = mods.Where(m => m.Other).Where(match).ToList() });
                groups.Add(new Group { Title = "DISABLED", Items = mods.Where(m => m.Kind == ModKind.Disabled).Where(match).ToList() });
            }
            else
            {
                groups.Add(new Group { Title = "MODS", Items = mods.Where(m => m.Kind == ModKind.Loaded && m.OptedIn).Where(match).ToList() });
                groups.Add(new Group { Title = "OTHER PLUGINS", Items = mods.Where(m => m.Other).Where(match).ToList() });
                groups.Add(new Group { Title = "DISABLED", Items = mods.Where(m => m.Kind == ModKind.Disabled).Where(match).ToList() });
                groups.Add(new Group { Title = "ON GITHUB", Items = mods.Where(m => m.Kind == ModKind.Available).Where(match).ToList() });
            }
            groups = groups.Where(g => g.Items.Count > 0).ToList();

            const float GH = 30, RH = 56;
            float contentH = groups.Sum(g => GH + g.Items.Count * (RH + 2)) + 8;
            var view = R(RailX, TopY + 96, RailW, BottomY - 8 - (TopY + 96));
            var inner = new Rect(0, 0, view.width - U(14), U(contentH));
            _listScroll = GUI.BeginScrollView(view, _listScroll, inner, false, false, GUIStyle.none, S.ScrollV);
            float y = 0;
            if (groups.Count == 0) S.Out(new Rect(U(4), U(10), inner.width, U(24)), "No mod matches.", S.Small, S.Desc);
            foreach (var g in groups)
            {
                S.Out(new Rect(U(4), y + U(6), inner.width, U(GH - 6)), g.Title, S.Section, S.White);
                y += U(GH);
                foreach (var m in g.Items)
                {
                    var r = new Rect(0, y, inner.width, U(RH));
                    DrawListRow(r, m);
                    y += U(RH + 2);
                }
            }
            GUI.EndScrollView();
        }

        private GUIStyle _leftBtn;
        private GUIStyle LeftBtn()
        {
            if (_leftBtn == null || _leftBtn.fontSize != S.BtnSmall.fontSize) { _leftBtn = new GUIStyle(S.BtnSmall) { alignment = TextAnchor.MiddleLeft }; }
            return _leftBtn;
        }

        private static readonly Dictionary<GUIStyle, GUIStyle> _transparent = new Dictionary<GUIStyle, GUIStyle>();
        private static GUIStyle Transparent(GUIStyle s)
        {
            GUIStyle t;
            if (_transparent.TryGetValue(s, out t)) return t;
            if (_transparent.Count > 16) _transparent.Clear();   // styles are rebuilt when the screen scale changes
            t = new GUIStyle(s) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            foreach (var st in new[] { t.normal, t.hover, t.active, t.focused, t.onNormal, t.onFocused }) { st.background = null; st.textColor = S.White; }
            _transparent[s] = t;
            return t;
        }

        private void DrawListRow(Rect r, ModEntry m)
        {
            bool sel = m.Guid == _selected;
            if (sel) GUI.Box(r, GUIContent.none, S.PlankStyle);
            if (GUI.Button(r, GUIContent.none, S.Invisible))
            {
                if (_selected != m.Guid) { ApplyPending(); _selected = m.Guid; _rightScroll = Vector2.zero; _dropdown = null; _settingSearch = ""; if (m.Kind != ModKind.Loaded && _tab == "settings") _tab = "about"; }
            }
            float ix = r.x + U(sel ? 12 : 10);
            var icon = new Rect(ix, r.y + (r.height - U(36)) / 2, U(36), U(36));
            DrawIcon(icon, m, sel ? S.Yellow : S.White, 0.62f);

            var st = Status(m);
            float textX = icon.xMax + U(12);
            float badgeW = 0;
            if (st.Badge != null)
            {
                var bs = S.Tiny.CalcSize(new GUIContent(st.Badge));
                badgeW = bs.x + U(12);
                var br = new Rect(r.xMax - badgeW - U(10), r.y + (r.height - U(20)) / 2, badgeW, U(20));
                S.Fill(br, Color.black);
                S.Fill(new Rect(br.x + 2, br.y + 2, br.width - 4, br.height - 4), st.BadgeBg);
                S.Label(br, st.Badge, S.Tiny, st.BadgeFg);
                badgeW += U(14);
            }
            float tw = r.xMax - textX - badgeW - U(6);
            float alpha = m.Kind == ModKind.Loaded ? 1f : 0.8f;
            S.Out(new Rect(textX, r.y + U(8), tw, U(22)), m.Name, S.ListName, Fade(sel ? S.Yellow : S.White, alpha));
            S.Out(new Rect(textX, r.y + U(29), tw, U(18)), st.Sub, S.ListSub, Fade(sel ? S.Hex("E8E0D2") : S.Hex("BDB3A6"), alpha));
        }

        private static Color Fade(Color c, float a) { return new Color(c.r, c.g, c.b, c.a * a); }

        private void DrawIcon(Rect r, ModEntry m, Color tint, float iconFrac)
        {
            S.SlotBox(r);
            var tex = m.Icon;
            var old = GUI.color;
            if (tex != null)
            {
                float k = r.width * iconFrac;
                GUI.color = tint;
                GUI.DrawTexture(new Rect(r.center.x - k / 2, r.center.y - k / 2, k, k), tex, ScaleMode.ScaleToFit);
                GUI.color = old;
            }
            else
            {
                var st = new GUIStyle(S.ListName) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(r.height * 0.36f) };
                S.Label(r, m.Initials, st, tint);
            }
        }

        // ---------------------------------------------------------------- status per mod
        private class ModStatus
        {
            public string Sub, Badge; public Color BadgeBg, BadgeFg;
            public bool Update, Ahead, Staged, Downloading, Blocked, NoSource;
            public string Staging;   // op kind
        }

        private ModStatus Status(ModEntry m)
        {
            var s = new ModStatus();
            var im = m.Index;
            var op = Updates.OpFor(m.Guid);
            float prog;
            s.Sub = m.Version != null ? "v" + m.Version : (im != null ? "v" + im.Version + " on GitHub" : "");
            if (m.Kind == ModKind.Disabled) s.Sub = "v" + m.Version + " · disabled";
            if (im != null && m.Kind == ModKind.Loaded)
            {
                int c = Updates.CompareVersions(im.Version, m.Version);
                if (im.Blocked) { s.Blocked = true; s.Badge = "BLOCKED"; s.BadgeBg = S.Hex("3A0D09"); s.BadgeFg = S.Hex("FF8A78"); }
                else if (c > 0) { s.Update = true; s.Badge = "↑ " + im.Version; s.BadgeBg = S.Yellow; s.BadgeFg = Color.black; }
                else if (c < 0) { s.Ahead = true; s.Sub += " · newer than GitHub"; }
                else s.Sub += " · up to date";
            }
            else if (m.Kind == ModKind.Loaded && Updates.HaveIndex) { s.NoSource = true; s.Sub += " · no update source"; }
            if (Updates.Downloading.TryGetValue(m.Guid, out prog)) { s.Downloading = true; s.Badge = "DOWNLOADING"; s.BadgeBg = S.Hex("111111"); s.BadgeFg = S.Yellow; }
            if (op != null)
            {
                s.Staged = true; s.Staging = MiniJson.Str(op, "op");
                bool bad = s.Staging == "remove" || s.Staging == "disable";
                s.Badge = s.Staging == "remove" ? "REMOVING" : s.Staging == "disable" ? "DISABLING" : "ON RESTART";
                s.BadgeBg = bad ? S.Hex("1E0907") : S.Hex("121509"); s.BadgeFg = bad ? S.Hex("FF8A78") : S.Green;
                if (s.Staging == "update" || s.Staging == "install") s.Sub = (m.Version != null ? "v" + m.Version + " → " : "") + MiniJson.Str(op, "version");
            }
            return s;
        }

        private bool IsUpdateListed(ModEntry m)
        {
            var s = Status(m);
            return m.Kind == ModKind.Loaded && (s.Update || s.Downloading || (s.Staged && s.Staging == "update"));
        }

        // ---------------------------------------------------------------- right pane
        private void DrawPane(ModEntry m)
        {
            var pane = R(PaneX, TopY, PaneW, BottomY - 8 - TopY);
            S.PitBox(pane);
            float x0 = PaneX + 28, w0 = PaneW - 56;
            var st = Status(m);
            var im = m.Index;

            // header
            var iconR = R(x0, TopY + 20, 66, 66);
            DrawIcon(iconR, m, S.Yellow, 0.6f);
            string name = (m.Name ?? "").ToUpperInvariant();
            string ver = m.Kind == ModKind.Available ? "not installed" : m.Kind == ModKind.Disabled ? "v" + m.Version + " · disabled" : "v" + m.Version;
            float verW = S.BodyBold.CalcSize(new GUIContent(ver)).x + U(4);
            var nameSize = S.H1.CalcSize(new GUIContent(name));
            float nameX = iconR.xMax + U(18);
            float nameW = Mathf.Min(nameSize.x + U(4), R(x0, 0, w0, 0).xMax - nameX - verW - U(12));
            // the name sits under the header buttons, so it gets the full width of the pane
            var nameR = new Rect(nameX, _wy + U(TopY + 50), nameW, U(32));
            S.Out(nameR, name, S.H1, S.White);
            S.Out(new Rect(nameR.xMax + U(10), nameR.y + U(6), verW, U(24)), ver, S.BodyBold, S.Yellow);
            var meta = new List<string>();
            if (im != null && !string.IsNullOrEmpty(im.Repo)) meta.Add("github.com/" + im.Repo);
            else if (m.Kind == ModKind.Loaded) meta.Add("no GitHub repo known");
            if (im != null && !string.IsNullOrEmpty(im.Author)) meta.Add("by " + im.Author);
            if (m.Config != null) meta.Add(Path.GetFileName(m.Config.ConfigFilePath));
            if (m.Other) meta.Add("GUID " + m.Guid);
            S.Label(new Rect(nameR.x, nameR.yMax + U(3), R(x0, 0, w0, 0).xMax - nameR.x, U(20)), string.Join("  ·  ", meta.ToArray()), S.Small, S.Sub);

            // header buttons (right)
            float bx = x0 + w0;
            var buttons = new List<KeyValuePair<string, Action>>();
            if (m.Kind == ModKind.Loaded && !m.Self && !st.Staged) buttons.Add(new KeyValuePair<string, Action>("REMOVE", () => { _removeOpen = true; _removeCfg = false; }));
            if (m.Kind == ModKind.Available && !st.Staged && !st.Downloading && im != null && im.Zip != null) buttons.Add(new KeyValuePair<string, Action>("INSTALL " + im.Version, () => Install(m)));
            if (m.Kind == ModKind.Disabled && !st.Staged) buttons.Add(new KeyValuePair<string, Action>("ENABLE", () => StageSimple(m, "enable", null, false)));
            if (im != null && !string.IsNullOrEmpty(im.Repo)) buttons.Add(new KeyValuePair<string, Action>("GITHUB", () => Application.OpenURL("https://github.com/" + im.Repo)));
            foreach (var b in buttons)
            {
                float bw = S.Btn.CalcSize(new GUIContent(b.Key)).x / _sc + 40;
                bx -= bw;
                var col = b.Key == "REMOVE" ? S.Red : b.Key.StartsWith("INSTALL") || b.Key == "ENABLE" ? S.Yellow : S.White;
                bool needsInstaller = b.Key != "GITHUB";
                if (S.PlankButton(R(bx, TopY + 12, bw, 36), b.Key, col, null, !needsInstaller || Updates.InstallerPresent)) b.Value();
                bx -= 6;
            }

            // banner
            float y = TopY + 112;
            float bh = DrawBanner(R(x0, y, w0, 62), m, st, im);
            if (bh > 0) y += bh + 8;

            // tabs
            int count = m.Entries.Count;
            if (m.Kind != ModKind.Loaded && _tab == "settings") _tab = "about";
            var tabs = new List<KeyValuePair<string, string>>();
            if (m.Kind == ModKind.Loaded) tabs.Add(new KeyValuePair<string, string>("settings", "SETTINGS " + count));
            tabs.Add(new KeyValuePair<string, string>("about", "ABOUT"));
            tabs.Add(new KeyValuePair<string, string>("notes", "WHAT'S NEW"));
            tabs.Add(new KeyValuePair<string, string>("files", "FILES"));
            float tx = x0;
            foreach (var t in tabs)
            {
                float tw = S.Tab.CalcSize(new GUIContent(t.Value)).x / _sc + 4;
                var tr = R(tx, y, tw, 38);
                bool on = _tab == t.Key;
                if (GUI.Button(tr, GUIContent.none, S.Invisible)) { _tab = t.Key; _rightScroll = Vector2.zero; }
                S.Out(tr, t.Value, S.Tab, on ? S.Yellow : S.Hex("BDB3A6"));
                if (on) S.Fill(new Rect(tr.x, tr.yMax - U(1), tr.width, U(3)), S.Yellow);
                tx += tw + 22;
            }
            if (_tab == "settings" && m.Kind == ModKind.Loaded && m.Entries.Count > 4)
            {
                var fr = R(x0 + w0 - 210, y + 2, 210, 30);
                GUI.SetNextControlName("apocasetter-findsetting");
                _settingSearch = GUI.TextField(fr, _settingSearch, 40, S.TextField);
                if (_settingSearch.Length == 0 && GUI.GetNameOfFocusedControl() != "apocasetter-findsetting")
                    S.Label(new Rect(fr.x + U(10), fr.y, fr.width, fr.height), "Find a setting", S.Small, S.Dim);
            }
            S.Fill(R(PaneX + 2, y + 39, PaneW - 4, 2), S.Line);
            y += 44;

            // content
            var area = R(x0, y, w0, (BottomY - 8 - 16) - y);
            GUILayout.BeginArea(area);
            _rightScroll = GUILayout.BeginScrollView(_rightScroll, false, false, GUIStyle.none, S.ScrollV, GUIStyle.none);
            float cw = area.width - U(16);
            if (_tab == "about") DrawAbout(m, im, cw);
            else if (_tab == "notes") DrawNotes(m, im, cw);
            else if (_tab == "files") DrawFiles(m, cw);
            else DrawSettings(m, im, cw);
            GUILayout.Space(U(12));
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        /// Returns the height used (virtual units), 0 when there's nothing to say.
        private float DrawBanner(Rect r, ModEntry m, ModStatus st, IndexMod im)
        {
            string title = null, sub = null, button = null; Color tc = S.Yellow; bool hazard = false, green = false, red = false;
            Action act = null;
            string err;
            float prog;
            if (Updates.Downloading.TryGetValue(m.Guid, out prog))
            {
                S.Fill(r, S.Hex("17130D"));
                S.Out(new Rect(r.x + U(14), r.y + U(8), r.width - U(28), U(24)), "DOWNLOADING " + (im != null && im.Zip != null ? im.Zip.Name : "") + "…", S.Section, S.White);
                var bar = new Rect(r.x + U(14), r.y + U(38), r.width - U(28), U(12));
                S.Fill(bar, S.Hex("8F8A83")); S.Fill(new Rect(bar.x + 2, bar.y + 2, bar.width - 4, bar.height - 4), S.Hex("050403"));
                S.HazardStrip(new Rect(bar.x + 2, bar.y + 2, (bar.width - 4) * Mathf.Clamp01(prog), bar.height - 4));
                return 62;
            }
            var op = Updates.OpFor(m.Guid);
            if (op != null)
            {
                var kind = MiniJson.Str(op, "op");
                green = kind == "update" || kind == "install" || kind == "enable";
                red = !green;
                title = kind == "update" ? "DOWNLOADED · INSTALLS ON THE NEXT START" : kind == "install" ? "INSTALLS ON THE NEXT START"
                      : kind == "enable" ? "ENABLED ON THE NEXT START" : kind == "disable" ? "DISABLED ON THE NEXT START" : "REMOVED ON THE NEXT START";
                sub = kind == "update" ? "v" + m.Version + " keeps running until you quit. The old files are backed up first; your settings stay as they are."
                    : kind == "install" ? m.Name + " " + MiniJson.Str(op, "version") + " is added before BepInEx loads plugins."
                    : kind == "enable" ? "Its files are moved back into BepInEx before plugins load."
                    : "It keeps running until you quit. The files go to BepInEx\\cache\\Apocasetter\\" + (kind == "disable" ? "disabled" : "removed") + "\\ and can be put back.";
                button = "UNDO"; act = () => Updates.Unstage(m.Guid);
                tc = green ? S.Green : S.Hex("FF8A78");
            }
            else if (Updates.Errors.TryGetValue(m.Guid, out err))
            {
                red = true; title = "DOWNLOAD FAILED"; sub = err; tc = S.Hex("FF8A78");
                if (im != null && im.Zip != null) { button = "TRY AGAIN"; act = () => Download(m, im); }
            }
            else if (st.Blocked)
            {
                red = true; title = "BLOCKED BY THE APOCASETTER INDEX"; tc = S.Hex("FF8A78");
                sub = string.IsNullOrEmpty(im.BlockedReason) ? "This mod was withdrawn from the index. Removing it is recommended." : im.BlockedReason;
            }
            else if (st.Update && im != null)
            {
                hazard = true;
                title = "UPDATE AVAILABLE  " + m.Version + " → " + im.Version;
                if (im.Zip != null)
                {
                    sub = "Released " + When(im.Published) + " · " + im.Zip.Name + " · " + Size(im.Zip.Size) + " · installs on the next game start, settings kept";
                    button = "UPDATE"; act = () => Download(m, im);
                }
                else { sub = "This release has no .zip attached, so it can't be installed from here."; button = "RELEASE PAGE"; act = () => Application.OpenURL(im.Page); }
            }
            else if (st.Ahead && im != null)
            {
                S.Fill(new Rect(r.x, r.y, r.width, U(34)), S.Hex("17130D"));
                S.Label(new Rect(r.x + U(14), r.y, r.width - U(28), U(34)), "Your copy (" + m.Version + ") is newer than the latest GitHub release (" + im.Version + "). Nothing to do.", S.Small, S.Desc);
                return 34;
            }
            else if (m.Kind == ModKind.Disabled)
            {
                S.Fill(new Rect(r.x, r.y, r.width, U(34)), S.Hex("17130D"));
                S.Label(new Rect(r.x + U(14), r.y, r.width - U(28), U(34)), "Disabled: its files are kept in BepInEx\\cache\\Apocasetter\\disabled\\. ENABLE puts them back at the next start.", S.Small, S.Desc);
                return 34;
            }
            if (title == null) return 0;

            S.Fill(r, Color.black);
            S.Fill(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4), red ? S.Hex("1E0907") : green ? S.Hex("121509") : S.Hex("17130D"));
            float tx = r.x + U(14);
            if (hazard) { S.HazardStrip(new Rect(r.x + 2, r.y + 2, U(16), r.height - 4)); tx = r.x + U(30); }
            float bw = button != null ? S.Btn.CalcSize(new GUIContent(button)).x + U(40) : 0;
            float textW = r.xMax - tx - bw - U(20);
            S.Out(new Rect(tx, r.y + U(7), textW, U(26)), title, S.Section, tc);
            S.Label(new Rect(tx, r.y + U(33), textW, U(24)), sub ?? "", S.Small, S.Desc);
            if (button != null && act != null)
            {
                bool needsInstaller = button == "UPDATE" || button == "TRY AGAIN";
                if (S.PlankButton(new Rect(r.xMax - bw - U(10), r.y + U(10), bw, U(42)), button, button == "UPDATE" ? S.Yellow : S.White, null, !needsInstaller || Updates.InstallerPresent)) act();
            }
            return 62;
        }

        private static string When(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return iso ?? "";
            var days = (DateTime.UtcNow.Date - t.Date).TotalDays;
            if (days < 1) return "today";
            if (days < 2) return "yesterday";
            return t.ToString("d MMM", CultureInfo.InvariantCulture);
        }

        private static string Size(long b)
        {
            if (b <= 0) return "";
            if (b < 1024 * 1024) return (b / 1024) + " KB";
            return (b / 1024f / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        // ---------------------------------------------------------------- settings tab
        private void DrawSettings(ModEntry m, IndexMod im, float cw)
        {
            if (m.Kind != ModKind.Loaded) { DrawAbout(m, im, cw); return; }
            if (m.Other && !_showOther.Contains(m.Guid))
            {
                GUILayout.Space(U(16));
                WrapLabel("This plugin doesn't list itself in Apocasetter (no \"Apocasetter = true\" in its config). Its settings can still be shown, read straight from its config file.", S.Body, S.Desc, cw);
                GUILayout.Space(U(8));
                float saw = U(BW("SHOW ANYWAY", 170));
                var br = GUILayoutUtility.GetRect(saw, U(40), GUILayout.Width(saw), GUILayout.Height(U(40)));
                if (S.PlankButton(br, "SHOW ANYWAY", S.White)) _showOther.Add(m.Guid);
                return;
            }
            if (m.Config == null || m.Entries.Count == 0)
            {
                GUILayout.Space(U(16));
                WrapLabel("This plugin has no settings.", S.Body, S.Desc, cw);
                return;
            }
            if (_keyUsers == null) _keyUsers = BuildKeyUsers();

            var q = _settingSearch.Trim().ToLowerInvariant();
            int shown = 0;
            var sections = new List<KeyValuePair<string, List<ConfigEntryBase>>>();
            foreach (var e in m.Entries)
            {
                var sec = e.Definition.Section;
                var list = sections.FirstOrDefault(kv => kv.Key == sec).Value;
                if (list == null) { list = new List<ConfigEntryBase>(); sections.Add(new KeyValuePair<string, List<ConfigEntryBase>>(sec, list)); }
                list.Add(e);
            }
            foreach (var kv in sections)
            {
                var rows = kv.Value.Where(e => q.Length == 0 || e.Definition.Key.ToLowerInvariant().Contains(q) || Human(e.Definition.Key).ToLowerInvariant().Contains(q)
                                               || Desc(e).ToLowerInvariant().Contains(q) || kv.Key.ToLowerInvariant().Contains(q)).ToList();
                if (rows.Count == 0) continue;
                GUILayout.Space(U(16));
                var hr = GUILayoutUtility.GetRect(cw, U(26), GUILayout.Width(cw), GUILayout.Height(U(26)));
                var titleW = S.Section.CalcSize(new GUIContent(kv.Key.ToUpperInvariant())).x;
                S.Out(new Rect(hr.x, hr.y, titleW + U(4), hr.height), kv.Key.ToUpperInvariant(), S.Section, S.White);
                var cnt = rows.Count + (rows.Count == 1 ? " setting" : " settings");
                var cntW = S.Small.CalcSize(new GUIContent(cnt)).x;
                S.Fill(new Rect(hr.x + titleW + U(14), hr.center.y, hr.width - titleW - cntW - U(28), U(2)), S.Line);
                S.Label(new Rect(hr.xMax - cntW, hr.y, cntW, hr.height), cnt, S.Small, S.Dim);

                bool collapsed = rows.Count > 40 && q.Length == 0 && !_showAll.Contains(m.Guid + "|" + kv.Key);
                if (collapsed)
                {
                    GUILayout.BeginHorizontal();
                    float sw = U(BW("SHOW ALL", 140));
                    var lr = GUILayoutUtility.GetRect(cw - sw - U(10), U(44), GUILayout.Width(cw - sw - U(10)), GUILayout.Height(U(44)));
                    S.Label(lr, rows.Count + " entries. Too many to list here; they're in the .cfg file.", S.Small, S.Desc);
                    var br = GUILayoutUtility.GetRect(sw, U(40), GUILayout.Width(sw), GUILayout.Height(U(40)));
                    if (S.PlankButton(br, "SHOW ALL", S.White)) _showAll.Add(m.Guid + "|" + kv.Key);
                    GUILayout.EndHorizontal();
                    continue;
                }
                foreach (var e in rows) { DrawEntry(m, e, cw); shown++; }
            }
            if (shown == 0) { GUILayout.Space(U(16)); WrapLabel("No setting matches.", S.Body, S.Sub, cw); }
        }

        private static readonly Type[] NumericTypes = { typeof(int), typeof(float), typeof(double), typeof(long), typeof(short), typeof(byte), typeof(decimal) };

        private void DrawEntry(ModEntry m, ConfigEntryBase e, float cw)
        {
            var t = e.SettingType;
            bool isKey = IsKeyEntry(e);
            object min, max;
            bool ranged = NumericTypes.Contains(t) && Range(e, out min, out max);
            float ctrlW = U(340), leftW = cw - ctrlW - U(24);
            string pending; bool isPending = _pending.TryGetValue(e, out pending);
            string err; _errors.TryGetValue(e, out err);
            var def = e.DefaultValue;
            bool modified = def != null && !Equals(def, e.BoxedValue);
            string desc = Desc(e);

            GUILayout.Space(U(12));
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(leftW));
            // title line
            var tr = GUILayoutUtility.GetRect(leftW, U(24), GUILayout.Width(leftW), GUILayout.Height(U(24)));
            float tx = tr.x;
            if (modified) { var oldM = GUI.matrix; var dr = new Rect(tx + U(1), tr.center.y - U(4), U(8), U(8)); GUIUtility.RotateAroundPivot(45, dr.center); S.Fill(dr, S.Yellow); GUI.matrix = oldM; tx += U(16); }
            string title = Human(e.Definition.Key) + (isPending ? "  *" : "");
            float titleW = Mathf.Min(S.BodyBold.CalcSize(new GUIContent(title)).x, leftW - (tx - tr.x));
            S.Label(new Rect(tx, tr.y, titleW, tr.height), title, S.BodyBold, S.White);
            S.Label(new Rect(tx + titleW + U(10), tr.y + U(2), leftW - titleW - U(10), tr.height), e.Definition.Key, S.Mono, S.Dim);
            // description: always the full text
            if (desc.Length > 0)
            {
                GUILayout.Space(U(4));
                float full = S.Body.CalcHeight(new GUIContent(desc), leftW) + U(2);
                var dr = GUILayoutUtility.GetRect(leftW, full, GUILayout.Width(leftW), GUILayout.Height(full));
                S.Label(dr, desc, S.Body, S.Desc);
                GUILayout.Space(U(10));
            }
            else GUILayout.Space(U(4));
            var cr = GUILayoutUtility.GetRect(leftW, U(20), GUILayout.Width(leftW), GUILayout.Height(U(20)));
            Chips(cr.x, cr, e, ranged, modified, desc);
            // key conflicts / errors
            if (isKey)
            {
                var c = Conflict(m, e);
                if (c != null)
                {
                    var r = GUILayoutUtility.GetRect(leftW, U(20), GUILayout.Width(leftW), GUILayout.Height(U(20)));
                    S.Label(r, "⚠ " + c, S.Small, S.Hex("FF8A78"));
                }
            }
            if (!string.IsNullOrEmpty(err))
            {
                var r = GUILayoutUtility.GetRect(leftW, U(20), GUILayout.Width(leftW), GUILayout.Height(U(20)));
                S.Label(r, "! " + err, S.Small, S.Hex("FF8A78"));
            }
            else if (isPending)
            {
                var r = GUILayoutUtility.GetRect(leftW, U(20), GUILayout.Width(leftW), GUILayout.Height(U(20)));
                S.Label(r, "Not saved yet: press Enter", S.Small, S.Yellow);
            }
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();

            // controls (right, top-aligned)
            var ctrl = GUILayoutUtility.GetRect(ctrlW, U(isKey ? 58 : 42), GUILayout.Width(ctrlW), GUILayout.Height(U(isKey ? 58 : 42)));
            float right = ctrl.xMax;
            // reset rivet
            var rr = new Rect(right - U(30), ctrl.y + U(5), U(30), U(30));
            var oldC = GUI.color;
            if (!modified) GUI.color = new Color(1, 1, 1, 0.3f);
            if (S.Rivet != null) GUI.DrawTexture(rr, S.Rivet);
            S.Label(rr, "↺", new GUIStyle(S.Tiny) { fontSize = Mathf.RoundToInt(U(15)) }, Color.black);
            GUI.color = oldC;
            if (modified && GUI.Button(rr, new GUIContent("", "Reset to default"), S.Invisible)) { _pending.Remove(e); SetNow(e, def); }
            right -= U(42);

            if (t == typeof(bool))
            {
                bool v = (bool)e.BoxedValue;
                float ow = Mathf.Max(BW("ON", 66), BW("OFF", 66));
                var off = new Rect(right - U(ow), ctrl.y, U(ow), U(38));
                var on = new Rect(off.x - U(ow + 2), ctrl.y, U(ow), U(38));
                if (S.PlankButton(on, "ON", v ? S.Yellow : S.White, null, true, v ? 1f : 0.4f) && !v) SetNow(e, true);
                if (S.PlankButton(off, "OFF", v ? S.White : S.Yellow, null, true, v ? 0.4f : 1f) && v) SetNow(e, false);
            }
            else if (isKey)
            {
                var kr = new Rect(right - U(110), ctrl.y, U(110), U(38));
                string val = e.GetSerializedValue();
                if (string.IsNullOrEmpty(val)) val = "None";
                bool waiting = _rebind == e;
                S.SlotBox(kr);
                if (Conflict(m, e) != null) { S.Fill(new Rect(kr.x, kr.yMax - U(3), kr.width, U(3)), S.Hex("E0452F")); }
                S.Label(kr, waiting ? "…" : val, S.Value, S.White);
                if (GUI.Button(kr, GUIContent.none, S.Invisible)) _rebind = waiting ? null : e;
                S.Label(new Rect(kr.x - U(20), kr.yMax + U(3), kr.width + U(40), U(14)), waiting ? "PRESS A KEY · ESC CANCELS · DEL CLEARS" : "CLICK TO REBIND", S.Tiny, S.Yellow);
            }
            else if (t.IsEnum || AcceptableList(e) != null)
            {
                var options = Options(e);
                var cur = e.GetSerializedValue();
                var nx = new Rect(right - U(40), ctrl.y, U(40), U(38));
                var vr = new Rect(nx.x - U(144), ctrl.y, U(142), U(38));
                var pv = new Rect(vr.x - U(42), ctrl.y, U(40), U(38));
                if (S.PlankButton(pv, "‹", S.White)) Cycle(e, options, cur, -1);
                if (S.PlankButton(vr, cur, S.Yellow, S.Value)) { _dropdown = _dropdown == e ? null : e; _dropScroll = Vector2.zero; }
                if (S.PlankButton(nx, "›", S.White)) Cycle(e, options, cur, +1);
            }
            else if (ranged)
            {
                float fmin = Convert.ToSingle(Range0(e)), fmax = Convert.ToSingle(Range1(e));
                float fv = isPending ? ParseF(pending, Convert.ToSingle(e.BoxedValue)) : Convert.ToSingle(e.BoxedValue);
                var field = new Rect(right - U(76), ctrl.y + U(2), U(76), U(34));
                var rail = new Rect(field.x - U(184), ctrl.y + U(17), U(168), U(4));
                S.Fill(new Rect(rail.x, rail.y + U(1), rail.width, rail.height), Color.black);
                S.Fill(rail, S.Hex("D9D4CB"));
                var thumb = KnobStyle();
                float nfv = GUI.HorizontalSlider(new Rect(rail.x, ctrl.y + U(9), rail.width, U(20)), fv, fmin, fmax, GUIStyle.none, thumb);
                if (Math.Abs(nfv - fv) > 1e-6f)
                {
                    _pending[e] = (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) ? nfv.ToString("0.###", CultureInfo.InvariantCulture) : Mathf.RoundToInt(nfv).ToString(CultureInfo.InvariantCulture);
                    _sliderDrag = e;
                }
                var text = isPending ? pending : e.GetSerializedValue();
                var nt = GUI.TextField(field, text, 16, NumField());
                if (nt != text) { if (nt == e.GetSerializedValue()) _pending.Remove(e); else _pending[e] = nt; }
            }
            else
            {
                var field = new Rect(right - U(250), ctrl.y + U(2), U(250), U(34));
                var text = isPending ? pending : e.GetSerializedValue();
                var nt = GUI.TextField(field, text, 400, S.TextField);
                if (nt != text) { if (nt == e.GetSerializedValue()) _pending.Remove(e); else _pending[e] = nt; }
            }
            GUILayout.EndHorizontal();

            // dropdown list under the row
            if (_dropdown == e)
            {
                var options = Options(e);
                var cur = e.GetSerializedValue();
                float lh = U(30);
                float h = Mathf.Min(U(240), options.Length * lh + U(8));
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                var box = GUILayoutUtility.GetRect(U(300), h, GUILayout.Width(U(300)), GUILayout.Height(h));
                GUILayout.Space(U(42));
                GUILayout.EndHorizontal();
                S.Fill(box, S.Hex("8F8A83")); S.Fill(new Rect(box.x + 2, box.y + 2, box.width - 4, box.height - 4), S.Hex("0A0807"));
                _dropScroll = GUI.BeginScrollView(new Rect(box.x + 4, box.y + 4, box.width - 8, box.height - 8), _dropScroll, new Rect(0, 0, box.width - U(24), options.Length * lh), false, false, GUIStyle.none, S.ScrollV);
                for (int i = 0; i < options.Length; i++)
                {
                    var o = options[i];
                    var r = new Rect(0, i * lh, box.width - U(24), lh);
                    if (o == cur) S.Fill(r, S.Hex("3A2E1A"));
                    S.Label(new Rect(r.x + U(10), r.y, r.width - U(10), r.height), o, S.ListName, o == cur ? S.Yellow : S.White);
                    if (GUI.Button(r, GUIContent.none, S.Invisible)) { if (o != cur) SetSerialized(e, o); _dropdown = null; }
                }
                GUI.EndScrollView();
            }

            GUILayout.Space(U(12));
            var sep = GUILayoutUtility.GetRect(cw, U(1), GUILayout.Width(cw), GUILayout.Height(U(1)));
            S.Fill(sep, S.Hex("221C17"));
        }

        private float Chips(float cx, Rect cr, ConfigEntryBase e, bool ranged, bool modified, string desc)
        {
            var def = e.DefaultValue;
            string defTxt = def is bool ? ((bool)def ? "ON" : "OFF") : (def == null || Convert.ToString(def, CultureInfo.InvariantCulture) == "" ? "EMPTY" : Convert.ToString(def, CultureInfo.InvariantCulture));
            cx = Chip(cx, cr, "DEFAULT " + defTxt.ToUpperInvariant(), S.Sub, S.Hex("3D352D"));
            if (ranged) cx = Chip(cx, cr, Range0(e) + " – " + Range1(e), S.Sub, S.Hex("3D352D"));
            if (Regex.IsMatch(desc, "restart", RegexOptions.IgnoreCase)) cx = Chip(cx, cr, "NEEDS A RESTART", S.Yellow, S.Hex("6B5F12"));
            if (modified) cx = Chip(cx, cr, "CHANGED", S.Hex("FFB27A"), S.Hex("7A3A1A"));
            return cx;
        }

        private float Chip(float x, Rect row, string text, Color fg, Color border)
        {
            var size = S.Tiny.CalcSize(new GUIContent(text));
            var r = new Rect(x, row.y + U(1), size.x + U(12), U(18));
            if (r.xMax > row.xMax) return x;
            S.Fill(r, border); S.Fill(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), S.Hex("0C0A09"));
            S.Label(r, text, S.Tiny, fg);
            return r.xMax + U(6);
        }

        private GUIStyle _knob, _numField;
        private GUIStyle KnobStyle()
        {
            if (_knob == null || Math.Abs(_knob.fixedWidth - U(18)) > 0.1f)
            {
                _knob = new GUIStyle(GUIStyle.none) { fixedWidth = U(18), fixedHeight = U(18) };
                _knob.normal.background = S.Knob; _knob.hover.background = S.Knob; _knob.active.background = S.Knob;
            }
            return _knob;
        }
        private GUIStyle NumField()
        {
            if (_numField != S.NumberField) _numField = S.NumberField;
            return _numField;
        }

        private void WrapLabel(string text, GUIStyle st, Color c, float w)
        {
            if (string.IsNullOrEmpty(text)) return;
            float h = st.CalcHeight(new GUIContent(text), w);
            var r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            S.Label(r, text, st, c);
        }

        private static string GuidName(string guid)
        {
            var m = Catalog.Find(guid);
            if (m != null) return m.Name;
            var im = Updates.Find(guid);
            return im != null ? im.Name : guid;
        }

        // ---------------------------------------------------------------- about
        private void DrawAbout(ModEntry m, IndexMod im, float cw)
        {
            GUILayout.Space(U(16));
            if (im != null && !string.IsNullOrEmpty(im.Summary)) WrapLabel(im.Summary, S.Body, S.Desc, cw);
            else WrapLabel("This mod isn't in the Apocasetter index, so there's no summary to show.", S.Body, S.Desc, cw);
            GUILayout.Space(U(14));

            if (im != null && !string.IsNullOrEmpty(im.Author)) AboutRow("Author", im.Author, cw);
            if (m.Kind != ModKind.Available) AboutRow("Installed", string.IsNullOrEmpty(m.Version) ? "?" : m.Version, cw);
            if (im != null && !string.IsNullOrEmpty(im.Version))
                AboutRow("Latest", im.Version + (string.IsNullOrEmpty(im.Published) ? "" : " · released " + When(im.Published)), cw);
            if (im != null && !string.IsNullOrEmpty(im.Repo)) AboutRow("GitHub", im.Repo, cw);
            if (im != null && im.Requires.Count > 0)
                AboutRow("Needs", string.Join(", ", im.Requires.Select(GuidName).ToArray()) + (m.Kind == ModKind.Available ? " (installed together)" : ""), cw);
            if (im != null && im.Optional.Count > 0) AboutRow("Works with", string.Join(", ", im.Optional.Select(GuidName).ToArray()), cw);
            if (m.Kind == ModKind.Loaded)
            {
                var deps = m.Dependents();
                if (deps.Count > 0) AboutRow("Used by", string.Join(", ", deps.Select(d => d.Key + (d.Value ? "" : " (optional)")).ToArray()), cw);
            }
            if (im != null && im.Tags.Count > 0) AboutRow("Tags", string.Join(", ", im.Tags.ToArray()), cw);
            AboutRow("Plugin ID", m.Guid, cw);

            if (m.Kind != ModKind.Loaded)
            {
                GUILayout.Space(U(14));
                WrapLabel(m.Kind == ModKind.Available ? "Not installed. Its settings appear here after it is installed and the game has started once."
                                                      : "Disabled. Its settings appear again once it is enabled.", S.Body, S.Desc, cw);
            }
        }

        private void AboutRow(string label, string value, float cw)
        {
            float lw = Mathf.Max(U(130), S.Small.CalcSize(new GUIContent(label)).x + U(16));
            float vw = cw - lw;
            float h = Mathf.Max(S.Body.CalcHeight(new GUIContent(value), vw), U(26)) + U(6);
            var r = GUILayoutUtility.GetRect(cw, h, GUILayout.Width(cw), GUILayout.Height(h));
            S.Label(new Rect(r.x, r.y + U(2), lw, U(24)), label, S.Small, S.Sub);
            S.Label(new Rect(r.x + lw, r.y, vw, h - U(6)), value, S.Body, S.Desc);
            S.Fill(new Rect(r.x, r.yMax - U(2), cw, 1), S.Line);
        }

        // ---------------------------------------------------------------- what's new
        private void DrawNotes(ModEntry m, IndexMod im, float cw)
        {
            GUILayout.Space(U(16));
            if (im == null || string.IsNullOrEmpty(im.Notes))
            {
                WrapLabel(im == null ? "This mod isn't in the Apocasetter index, so there are no release notes to show." : "The latest release has no notes.", S.Body, S.Desc, cw);
                return;
            }
            var hr = GUILayoutUtility.GetRect(cw, U(30), GUILayout.Width(cw), GUILayout.Height(U(30)));
            string head = (im.Name + " " + im.Version).ToUpperInvariant();
            S.Out(new Rect(hr.x, hr.y, cw, hr.height), head, S.Section, S.Yellow);
            S.Label(new Rect(hr.x + S.Section.CalcSize(new GUIContent(head)).x + U(14), hr.y, cw, hr.height), "released " + When(im.Published), S.Small, S.Sub);
            GUILayout.Space(U(8));
            foreach (var raw in im.Notes.Replace("\r", "").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) { GUILayout.Space(U(6)); continue; }
                bool heading = line.StartsWith("#") || (line.StartsWith("**") && line.EndsWith("**"));
                bool bullet = line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ");
                var text = line.TrimStart('#', ' ');
                if (bullet) text = "•  " + text.TrimStart().Substring(2);
                text = text.Replace("**", "").Replace("`", "");
                if (heading) { GUILayout.Space(U(4)); WrapLabel(text.ToUpperInvariant(), S.Section, S.White, cw); }
                else WrapLabel(text, S.Body, S.Desc, cw);
            }
            if (!string.IsNullOrEmpty(im.Page))
            {
                GUILayout.Space(U(10));
                float rw = U(BW("RELEASE PAGE", 200));
                var br = GUILayoutUtility.GetRect(rw, U(40), GUILayout.Width(rw), GUILayout.Height(U(40)));
                if (S.PlankButton(br, "RELEASE PAGE", S.White)) Application.OpenURL(im.Page);
            }
        }

        // ---------------------------------------------------------------- files
        private void DrawFiles(ModEntry m, float cw)
        {
            GUILayout.Space(U(16));
            if (m.Kind == ModKind.Available) { WrapLabel("Nothing installed yet. Install puts the zip's files into BepInEx\\plugins\\ at the next start.", S.Body, S.Desc, cw); return; }
            if (m.Kind == ModKind.Disabled) { WrapLabel("Its files are in " + Rel(m.DisabledInfo.Dir) + " until it is enabled again.", S.Body, S.Desc, cw); return; }
            WrapLabel("What Update and Remove touch. An update unpacks the new zip over these files and leaves anything the zip doesn't contain alone, so files you added stay.", S.Body, S.Desc, cw);
            GUILayout.Space(U(8));
            var rows = new List<string[]>();
            var folder = m.PluginFolder;
            if (folder != null)
            {
                rows.Add(new[] { Rel(folder) + "\\", "the mod's folder", "replaced on update" });
                try
                {
                    foreach (var p in Directory.GetFileSystemEntries(folder).OrderBy(x => x).Take(30))
                        rows.Add(new[] { "    " + Path.GetFileName(p) + (Directory.Exists(p) ? "\\" : ""), Directory.Exists(p) ? "folder" : SizeOf(p), "" });
                }
                catch { }
            }
            else foreach (var p in m.PluginPaths()) rows.Add(new[] { Rel(p), SizeOf(p), "replaced on update" });
            foreach (var p in m.ConfigPaths()) rows.Add(new[] { Rel(p) + (Directory.Exists(p) ? "\\" : ""), p.EndsWith(".cfg") ? "settings" : "the mod's data", "kept" });
            foreach (var r in rows)
            {
                var lr = GUILayoutUtility.GetRect(cw, U(26), GUILayout.Width(cw), GUILayout.Height(U(26)));
                S.Label(new Rect(lr.x, lr.y, cw * 0.62f, lr.height), r[0], S.ListName, S.White);
                S.Label(new Rect(lr.x + cw * 0.62f, lr.y, cw * 0.22f, lr.height), r[1], S.Small, S.Sub);
                if (r[2].Length > 0) S.Label(new Rect(lr.x + cw * 0.84f, lr.y, cw * 0.16f, lr.height), r[2].ToUpperInvariant(), S.Tiny, r[2] == "kept" ? S.Green : S.Desc);
                S.Fill(new Rect(lr.x, lr.yMax, cw, 1), S.Hex("221C17"));
            }
            GUILayout.Space(U(14));
            GUILayout.BeginHorizontal();
            float w1 = U(BW("OPEN PLUGIN FOLDER", 210)), w2 = U(BW("OPEN CONFIG FILE", 190)), w3 = U(BW("DISABLE", 120));
            var b1 = GUILayoutUtility.GetRect(w1, U(40), GUILayout.Width(w1), GUILayout.Height(U(40)));
            if (S.PlankButton(b1, "OPEN PLUGIN FOLDER", S.White)) OpenPath(folder ?? Paths.PluginPath);
            GUILayout.Space(U(6));
            var b2 = GUILayoutUtility.GetRect(w2, U(40), GUILayout.Width(w2), GUILayout.Height(U(40)));
            if (S.PlankButton(b2, "OPEN CONFIG FILE", S.White, null, m.Config != null)) OpenPath(m.Config.ConfigFilePath);
            if (!m.Self)
            {
                GUILayout.Space(U(6));
                var b3 = GUILayoutUtility.GetRect(w3, U(40), GUILayout.Width(w3), GUILayout.Height(U(40)));
                if (S.PlankButton(b3, "DISABLE", S.White, null, Updates.InstallerPresent && Updates.OpFor(m.Guid) == null)) StageSimple(m, "disable", null, false);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static string SizeOf(string p)
        {
            try { if (File.Exists(p)) return Size(new FileInfo(p).Length); } catch { }
            return "";
        }

        private static string Rel(string p)
        {
            var root = Path.GetFullPath(Paths.BepInExRootPath).TrimEnd('\\', '/');
            var f = Path.GetFullPath(p);
            return f.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? "BepInEx" + f.Substring(root.Length) : f;
        }

        private static void OpenPath(string p)
        {
            try { Application.OpenURL("file:///" + p.Replace('\\', '/')); } catch (Exception e) { Plugin.Log.LogWarning("Open " + p + ": " + e.Message); }
        }

        // ---------------------------------------------------------------- bottom bar
        private void DrawBottom(ModEntry mod)
        {
            float y = BottomY + 4;
            float right = WW - FR - 16;
            if (Updates.Ops.Count > 0)
            {
                float qw = BW("QUIT GAME TO APPLY", 226);
                var bar = R(RailX, y, right - RailX - qw - 10, 40);
                S.Fill(bar, Color.black);
                S.Fill(new Rect(bar.x + 2, bar.y + 2, bar.width - 4, bar.height - 4), S.Hex("0C0A08"));
                S.HazardStrip(new Rect(bar.x + 2, bar.y + 2, U(14), bar.height - 4));
                var items = Updates.Ops.Select(o =>
                {
                    var k = MiniJson.Str(o, "op"); var n = MiniJson.Str(o, "name");
                    return k == "update" ? n + " → " + MiniJson.Str(o, "version") : k + " " + n;
                }).ToArray();
                var uw = S.Small.CalcSize(new GUIContent("Undo all")).x;
                S.Out(new Rect(bar.x + U(26), bar.y, bar.width - U(40) - uw, bar.height), "WAITING FOR A RESTART: " + string.Join(" · ", items), S.BodyBold, S.Yellow);
                var ur = new Rect(bar.xMax - uw - U(12), bar.y, uw, bar.height);
                S.Label(ur, "Undo all", S.Small, S.White);
                S.Fill(new Rect(ur.x, ur.center.y + U(8), uw, 1), S.White);
                if (GUI.Button(ur, GUIContent.none, S.Invisible)) Updates.UnstageAll();
                if (GameMenu.InGame) S.Out(R(right - qw, y, qw, 40), "Save, then restart the game", S.Small, S.Desc);
                else if (S.PlankButton(R(right - qw, y - 1, qw, 42), "QUIT GAME TO APPLY", S.Yellow)) Application.Quit();
                return;
            }
            string status = Time.unscaledTime < _statusUntil ? _status : LastResultsText();
            if (string.IsNullOrEmpty(status))
            {
                status = _pending.Count > 0 ? _pending.Count + " unsaved change(s): press Enter" : "Changes are saved as you make them.";
                if (!Updates.InstallerPresent) status = "Apocasetter.Installer.dll is missing from BepInEx\\patchers: updates and removals are off.";
            }
            float fw = BW("OPEN CONFIG FOLDER", 214), lw = BW("RELOAD FROM FILE", 190);
            float x = right - fw;
            if (S.PlankButton(R(x, y - 1, fw, 42), "OPEN CONFIG FOLDER", S.White)) OpenPath(Paths.ConfigPath);
            x -= lw + 6;
            if (S.PlankButton(R(x, y - 1, lw, 42), "RELOAD FROM FILE", S.White, null, mod != null && mod.Config != null))
            { _pending.Clear(); _errors.Clear(); mod.Config.Reload(); SetStatus("Reloaded " + Path.GetFileName(mod.Config.ConfigFilePath)); }
            S.Out(R(RailX, y, x - RailX - 12, 40), status, S.Small, Updates.InstallerPresent ? S.Desc : S.Hex("FF8A78"));
        }

        private string LastResultsText()
        {
            if (Updates.LastResults.Count == 0) return null;
            return "Last start: " + string.Join(" · ", Updates.LastResults.Select(r =>
            {
                var ok = MiniJson.Bool(r, "ok", false); var k = MiniJson.Str(r, "op"); var n = MiniJson.Str(r, "name");
                return ok ? (k == "update" ? n + " updated to " + MiniJson.Str(r, "version") : n + " " + (k == "install" ? "installed" : k + "d"))
                          : n + " " + k + " FAILED: " + MiniJson.Str(r, "message");
            }).ToArray());
        }

        // ---------------------------------------------------------------- remove dialog
        private void DrawRemoveDialog(ModEntry m)
        {
            S.Fill(R(0, 0, WW, WH), new Color(0.02f, 0.015f, 0.01f, 0.78f));
            var plugin = m.PluginPaths();
            var cfgs = m.ConfigPaths();
            var deps = m.Dependents();
            float rw = BW("REMOVE", 130), dw = BW("DISABLE INSTEAD", 190), cw2 = BW("CANCEL", 120);
            float w = Mathf.Max(620, rw + dw + cw2 + 12 + 60);
            string intro = m.Name + " " + m.Version + " keeps running until you quit. On the next start its files are moved to BepInEx\\cache\\Apocasetter\\removed\\, so you can put them back.";
            float introH = S.Body.CalcHeight(new GUIContent(intro), U(w - 76)) / _sc;
            float h = 70 + introH + 18 + plugin.Count * 24 + 24 + 54 + (deps.Count > 0 ? 50 : 0) + 70;
            float x = (WW - w) / 2, y = (WH - h) / 2;
            var box = R(x, y, w, h);
            S.RustBack(box);
            if (S.Chains != null) GUI.DrawTexture(R(x, y, w, 64), S.Chains, ScaleMode.ScaleAndCrop);
            S.Frame(box, U(16));
            S.Out(R(x + 30, y + 22, w - 60, 40), "REMOVE " + m.Name.ToUpperInvariant() + "?", S.H1, S.White);
            float cy = y + 72;
            S.Out(R(x + 30, cy, w - 60, introH), intro, S.Body, S.White);
            cy += introH + 10;
            var pit = R(x + 30, cy, w - 60, plugin.Count * 24 + 16);
            S.Fill(pit, S.Pit);
            for (int i = 0; i < plugin.Count; i++)
                S.Label(R(x + 42, cy + 8 + i * 24, w - 84, 24), Rel(plugin[i]) + (Directory.Exists(plugin[i]) ? "\\  (whole folder)" : ""), S.ListName, S.White);
            cy += plugin.Count * 24 + 26;
            // checkbox
            var cb = R(x + 30, cy, 22, 22);
            S.SlotBox(cb);
            if (_removeCfg) S.Label(cb, "✓", new GUIStyle(S.ListName) { alignment = TextAnchor.MiddleCenter }, S.Yellow);
            var cl = R(x + 62, cy - 4, w - 92, 50);
            S.Out(new Rect(cl.x, cl.y, cl.width, U(24)), "Also remove its settings and saved data", S.BodyBold, S.White);
            S.Label(new Rect(cl.x, cl.y + U(24), cl.width, U(22)), cfgs.Count == 0 ? "(none found)" : string.Join(", ", cfgs.Select(Rel).ToArray()), S.Small, S.Desc);
            if (GUI.Button(new Rect(cb.x, cb.y, cl.xMax - cb.x, U(46)), GUIContent.none, S.Invisible)) _removeCfg = !_removeCfg;
            cy += 54;
            if (deps.Count > 0)
            {
                var wr = R(x + 30, cy, w - 60, 42);
                S.Fill(wr, S.Hex("0C0A08"));
                S.HazardStrip(new Rect(wr.x, wr.y, U(12), wr.height));
                var hard = deps.Where(d => d.Value).Select(d => d.Key).ToArray();
                var soft = deps.Where(d => !d.Value).Select(d => d.Key).ToArray();
                string warn = hard.Length > 0 ? string.Join(", ", hard) + (hard.Length == 1 ? " needs it and won't load without it." : " need it and won't load without it.")
                                              : string.Join(", ", soft) + " " + (soft.Length == 1 ? "has" : "have") + " extra features for it and keep working without it.";
                S.Label(new Rect(wr.x + U(22), wr.y, wr.width - U(30), wr.height), warn, S.Small, S.Yellow);
                cy += 50;
            }
            float bx = x + w - 30;
            bx -= rw; bool doRemove = S.PlankButton(R(bx, cy + 8, rw, 44), "REMOVE", S.Red);
            bx -= dw + 6; bool doDisable = S.PlankButton(R(bx, cy + 8, dw, 44), "DISABLE INSTEAD", S.White);
            bx -= cw2 + 6; bool cancel = S.PlankButton(R(bx, cy + 8, cw2, 44), "CANCEL", S.White);
            if (cancel) _removeOpen = false;
            if (doDisable) { StageSimple(m, "disable", null, false); _removeOpen = false; }
            if (doRemove) { StageSimple(m, "remove", null, _removeCfg); _removeOpen = false; }
        }

        // ---------------------------------------------------------------- actions
        private void Download(ModEntry m, IndexMod im)
        {
            if (!Updates.InstallerPresent) { SetStatus("The installer is missing; reinstall Apocasetter."); return; }
            var op = new Dictionary<string, object>
            {
                { "op", m.Kind == ModKind.Available ? "install" : "update" }, { "guid", m.Guid }, { "name", m.Name },
                { "fromVersion", m.Version ?? "" }, { "oldDll", m.Location ?? "" }
            };
            StartCoroutine(Updates.Download(op, im));
        }

        private void Install(ModEntry m)
        {
            var im = m.Index;
            if (im == null) return;
            Download(m, im);
            foreach (var req in im.Requires)
            {
                if (Catalog.All().Any(x => x.Guid == req && x.Kind == ModKind.Loaded) || Updates.OpFor(req) != null) continue;
                var rm = Catalog.Find(req);
                var rim = Updates.Find(req);
                if (rm != null && rim != null && rim.Zip != null) Download(rm, rim);
            }
        }

        private void StageSimple(ModEntry m, string kind, string unused, bool removeConfig)
        {
            if (!Updates.InstallerPresent) { SetStatus("The installer is missing; reinstall Apocasetter."); return; }
            var op = new Dictionary<string, object> { { "op", kind }, { "guid", m.Guid }, { "name", m.Name }, { "version", m.Version ?? "" } };
            if (kind == "remove" || kind == "disable")
            {
                op["paths"] = m.PluginPaths().Cast<object>().ToList();
                op["configPaths"] = m.ConfigPaths().Cast<object>().ToList();
                op["removeConfig"] = removeConfig;
            }
            Updates.Stage(op);
        }

        // ---------------------------------------------------------------- MODS badge + title-screen notice (window closed)
        private GUIStyle _badgeFont;
        private void DrawMenuExtras()
        {
            if (!GameMenu.ButtonOnScreen && !GameMenu.HasButton && (GameMenu.MenuVisible || (!GameMenu.InGame && Cursor.visible))) DrawFallbackButton();
            if (Plugin.UpdateNoticeEntry != null && !Plugin.UpdateNoticeEntry.Value) return;
            var rects = GameMenu.VisibleButtonRects();
            if (rects.Count == 0) return;
            var mods = Catalog.All();
            var updates = mods.Where(m => Status(m).Update).ToList();
            float sc = Mathf.Clamp(Mathf.Min(Screen.width / 1280f, Screen.height / 720f), 0.6f, 4f);
            S.Ensure(sc);
            _sc = sc;
            if (updates.Count > 0)
                foreach (var r in rects)
                {
                    // centred on the button's right edge, sized from the button, number in the game's menu font
                    float d = Mathf.Round(Mathf.Clamp(r.height * 0.64f, U(22), U(42)));
                    var b = new Rect(Mathf.Round(r.xMax - d * 0.65f), Mathf.Round(r.center.y - d / 2), d, d);
                    var old = GUI.color; GUI.color = S.Yellow;
                    if (S.Knob != null) GUI.DrawTexture(b, S.Knob); else S.Fill(b, S.Yellow);
                    GUI.color = old;
                    int fs = Mathf.Max(8, Mathf.RoundToInt(d * 0.6f));
                    if (_badgeFont == null || _badgeFont.fontSize != fs || _badgeFont.font != S.Tab.font)
                    {
                        _badgeFont = new GUIStyle(S.Tab) { fontSize = fs, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow, padding = new RectOffset() };
                        GameSkin.CenterText(_badgeFont, "0123456789");
                    }
                    S.Label(b, updates.Count.ToString(), _badgeFont, Color.black);
                }
            // once per game start, on the title screen only
            if (_toastDismissed || GameMenu.InGame || (updates.Count == 0 && Updates.LastResults.Count == 0)) return;
            var anchor = rects[0];
            float w = U(Mathf.Max(340, 2 * Mathf.Max(BW("LATER", 150), BW("OPEN MODS", 150)) + 32));
            var lines = new List<KeyValuePair<string, string>>();
            foreach (var r in Updates.LastResults)
            {
                bool ok = MiniJson.Bool(r, "ok", false);
                lines.Add(new KeyValuePair<string, string>(MiniJson.Str(r, "name"), ok ? (MiniJson.Str(r, "op") == "update" ? "updated to " + MiniJson.Str(r, "version") : MiniJson.Str(r, "op") + " done") : "FAILED"));
            }
            foreach (var m in updates.Take(6)) lines.Add(new KeyValuePair<string, string>(m.Name, m.Version + " → " + m.Index.Version));
            float h = U(44 + 20 + lines.Count * 22 + 64);
            var box = new Rect(Mathf.Min(anchor.xMax - w, Screen.width - w - U(8)), anchor.yMax + U(12), w, h);
            S.RustBack(box);
            if (S.Chains != null) GUI.DrawTexture(new Rect(box.x, box.y, box.width, U(46)), S.Chains, ScaleMode.ScaleAndCrop);
            S.Frame(box, U(12));
            string title = updates.Count > 0 ? updates.Count + (updates.Count == 1 ? " MOD UPDATE" : " MOD UPDATES") : "MODS CHANGED";
            S.Out(new Rect(box.x + U(18), box.y + U(14), w - U(36), U(30)), title, S.Section, S.Yellow, 1.3f);
            var pit = new Rect(box.x + U(14), box.y + U(50), w - U(28), U(12 + lines.Count * 22));
            S.Fill(pit, S.Pit);
            for (int i = 0; i < lines.Count; i++)
            {
                var lr = new Rect(pit.x + U(10), pit.y + U(6 + i * 22), pit.width - U(20), U(22));
                S.Label(lr, lines[i].Key, S.ListName, S.White);
                var rs = new GUIStyle(S.ListName) { alignment = TextAnchor.MiddleRight };
                S.Label(lr, lines[i].Value, rs, lines[i].Value == "FAILED" ? S.Red : S.Yellow);
            }
            float by = pit.yMax + U(10);
            if (S.PlankButton(new Rect(box.x + U(14), by, (w - U(32)) / 2, U(40)), "LATER", S.White)) _toastDismissed = true;
            if (S.PlankButton(new Rect(box.x + U(18) + (w - U(32)) / 2, by, (w - U(32)) / 2, U(40)), "OPEN MODS", S.Yellow)) { _toastDismissed = true; _filter = updates.Count > 0 ? "updates" : "all"; Open(); }
        }

        private GUIStyle _hintStyle;
        // Used only when no native menu button could be cloned.
        private void DrawFallbackButton()
        {
            Theme.Apply();
            if (_hintStyle == null || _hintStyle.font != GUI.skin.font)
                _hintStyle = new GUIStyle(GUI.skin.button) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, fixedHeight = 0 };
            var r = new Rect(Screen.width - 190, 24, 160, 46);
            if (GUI.Button(r, "MODS", _hintStyle)) Open();
        }

        // ---------------------------------------------------------------- keys
        private static readonly Regex KeyNameRx = new Regex(@"key|toggle|hotkey", RegexOptions.IgnoreCase);

        private static bool IsKeyEntry(ConfigEntryBase e)
        {
            var t = e.SettingType;
            if (t == typeof(Key) || t == typeof(KeyCode)) return true;
            if (t != typeof(string)) return false;
            if (!KeyNameRx.IsMatch(e.Definition.Key) && !KeyNameRx.IsMatch(e.Definition.Section)) return false;
            var v = e.GetSerializedValue();
            Key k;
            return string.IsNullOrEmpty(v) || Enum.TryParse(NormalizeKey(v), true, out k);
        }

        private static string NormalizeKey(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            var m = Regex.Match(v, @"^Alpha(\d)$");
            if (m.Success) return "Digit" + m.Groups[1].Value;
            if (v == "Return") return "Enter";
            if (v == "LeftControl") return "LeftCtrl";
            if (v == "RightControl") return "RightCtrl";
            return v;
        }

        private Dictionary<string, List<string>> BuildKeyUsers()
        {
            var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in Catalog.All().Where(x => x.Kind == ModKind.Loaded && x.Config != null))
                foreach (var e in m.Entries)
                {
                    if (!IsKeyEntry(e)) continue;
                    var v = NormalizeKey(e.GetSerializedValue());
                    if (string.IsNullOrEmpty(v) || v == "None") continue;
                    List<string> l;
                    if (!d.TryGetValue(v, out l)) d[v] = l = new List<string>();
                    l.Add(m.Guid + "\u0001" + m.Name + " › " + e.Definition.Key);
                }
            return d;
        }

        private string Conflict(ModEntry m, ConfigEntryBase e)
        {
            if (_keyUsers == null) _keyUsers = BuildKeyUsers();
            var v = NormalizeKey(e.GetSerializedValue());
            if (string.IsNullOrEmpty(v) || v == "None") return null;
            var me = m.Guid + "\u0001" + m.Name + " › " + e.Definition.Key;
            var others = new List<string>();
            string game;
            if (GameKeys.TryGetValue(v, out game)) others.Add(game);
            List<string> l;
            if (_keyUsers.TryGetValue(v, out l)) others.AddRange(l.Where(x => x != me).Select(x => x.Substring(x.IndexOf('\u0001') + 1)));
            return others.Count == 0 ? null : v + " is also used by " + string.Join(", ", others.ToArray());
        }

        private void SetKey(ConfigEntryBase e, Key k)
        {
            var t = e.SettingType;
            string name = k.ToString();
            if (t == typeof(KeyCode))
            {
                var m = Regex.Match(name, @"^Digit(\d)$");
                if (m.Success) name = "Alpha" + m.Groups[1].Value;
                else if (k == Key.Enter) name = "Return";
                else if (k == Key.LeftCtrl) name = "LeftControl";
                else if (k == Key.RightCtrl) name = "RightControl";
                KeyCode kc;
                if (k == Key.None) name = "None";
                else if (!Enum.TryParse(name, true, out kc)) { SetStatus("That key can't be stored in " + e.Definition.Key); return; }
            }
            else if (t == typeof(string) && k == Key.None) name = "";
            SetSerialized(e, name);
            _keyUsers = null;
        }

        // ---------------------------------------------------------------- entry helpers
        private static string Desc(ConfigEntryBase e) { return e.Description != null && e.Description.Description != null ? e.Description.Description : ""; }

        private static string Human(string k)
        {
            if (k.IndexOf(' ') >= 0) return char.ToUpperInvariant(k[0]) + k.Substring(1);
            if (Regex.IsMatch(k, "^[A-Z0-9-]+$")) return k;
            var fix = new Dictionary<string, string> { { "Npc", "NPC" }, { "Ai", "AI" }, { "Hud", "HUD" }, { "Fsms", "FSMs" }, { "Json", "JSON" }, { "Ui", "UI" } };
            var words = Regex.Replace(k, "([a-z0-9])([A-Z])", "$1 $2").Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string f;
                if (fix.TryGetValue(words[i], out f)) words[i] = f;
                else if (i > 0) words[i] = words[i].ToLowerInvariant();
            }
            return string.Join(" ", words);
        }

        private static string[] Options(ConfigEntryBase e)
        {
            var t = e.SettingType;
            return t.IsEnum ? Enum.GetNames(t) : (AcceptableList(e) ?? new object[0]).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToArray();
        }

        private static float ParseF(string s, float fb) { float f; return s != null && float.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fb; }

        private void Cycle(ConfigEntryBase e, string[] options, string cur, int dir)
        {
            if (options.Length == 0) return;
            int i = Array.IndexOf(options, cur);
            i = i < 0 ? 0 : (i + dir + options.Length) % options.Length;
            SetSerialized(e, options[i]);
        }

        private static bool Range(ConfigEntryBase e, out object min, out object max)
        {
            min = max = null;
            var av = e.Description != null ? e.Description.AcceptableValues : null;
            if (av == null) return false;
            var pi = av.GetType().GetProperty("MinValue"); var pa = av.GetType().GetProperty("MaxValue");
            if (pi == null || pa == null) return false;
            min = pi.GetValue(av, null); max = pa.GetValue(av, null);
            return min != null && max != null;
        }
        private static string Range0(ConfigEntryBase e) { object a, b; return Range(e, out a, out b) ? Convert.ToString(a, CultureInfo.InvariantCulture) : ""; }
        private static string Range1(ConfigEntryBase e) { object a, b; return Range(e, out a, out b) ? Convert.ToString(b, CultureInfo.InvariantCulture) : ""; }

        private static object[] AcceptableList(ConfigEntryBase e)
        {
            var av = e.Description != null ? e.Description.AcceptableValues : null;
            if (av == null) return null;
            var p = av.GetType().GetProperty("AcceptableValues");
            if (p == null) return null;
            var arr = p.GetValue(av, null) as Array;
            return arr == null ? null : arr.Cast<object>().ToArray();
        }

        // ---------------------------------------------------------------- applying
        private void SetStatus(string s) { _status = s; _statusUntil = Time.unscaledTime + 4f; }

        private void SetNow(ConfigEntryBase e, object value)
        {
            try { e.BoxedValue = value; _errors.Remove(e); Persist(e); }
            catch (Exception ex) { _errors[e] = ex.Message; }
        }

        // BepInEx parses floats with the invariant culture and AllowThousands: "0,5" would silently become 5. Our settings never use
        // thousands separators, so a comma typed on a decimal-comma PC is taken as the decimal point.
        private static string Normalize(ConfigEntryBase e, string value)
        {
            if (value == null) return null;
            var t = e.SettingType;
            return t == typeof(float) || t == typeof(double) || t == typeof(decimal) ? value.Trim().Replace(',', '.') : value;
        }

        private void SetSerialized(ConfigEntryBase e, string value)
        {
            try { e.SetSerializedValue(Normalize(e, value)); _errors.Remove(e); _pending.Remove(e); Persist(e); }
            catch (Exception ex) { _errors[e] = ex.Message; }
        }

        private void ApplyPending()
        {
            if (_pending.Count == 0) return;
            int ok = 0, bad = 0;
            var files = new HashSet<ConfigFile>();
            foreach (var kv in _pending.ToList())
            {
                try { kv.Key.SetSerializedValue(Normalize(kv.Key, kv.Value)); _errors.Remove(kv.Key); _pending.Remove(kv.Key); files.Add(kv.Key.ConfigFile); ok++; }
                catch (Exception ex) { _errors[kv.Key] = ex.Message; bad++; }
            }
            foreach (var f in files) SaveFile(f);
            SetStatus(bad == 0 ? "Saved " + ok + " setting(s)" : ok + " saved, " + bad + " invalid");
        }

        private void Persist(ConfigEntryBase e)
        {
            SaveFile(e.ConfigFile);
            SetStatus("Saved " + e.Definition.Key);
            _keyUsers = null;
        }

        private static void SaveFile(ConfigFile cfg)
        {
            // SaveOnConfigSet normally writes on every set; force it in case a plugin turned that off
            try { cfg.Save(); } catch (Exception ex) { Plugin.Log.LogWarning("Config save failed: " + ex.Message); }
        }
    }
}
