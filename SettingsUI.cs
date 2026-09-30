using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Apocasetter
{
    // =====================================================================
    //  Which plugins to show: any whose .cfg contains  Apocasetter = true
    // =====================================================================
    public class ModEntry
    {
        public string Guid, Name, Version;
        public ConfigFile Config;
        public List<ConfigEntryBase> Entries = new List<ConfigEntryBase>();
    }

    public static class ModCatalog
    {
        private static readonly Regex OptInRx = new Regex(@"^\s*Apocasetter\s*=\s*true\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static List<ModEntry> _mods;

        public static void Invalidate() { _mods = null; }

        public static List<ModEntry> Mods()
        {
            if (_mods != null) return _mods;
            var list = new List<ModEntry>();
            foreach (var kv in Chainloader.PluginInfos)
            {
                var info = kv.Value;
                // the game destroys plugin GameObjects on scene load, so Unity's == null would be true; the managed object (and its Config) is still fine
                if (info == null || ReferenceEquals(info.Instance, null)) continue;
                ConfigFile cfg;
                try { cfg = info.Instance.Config; } catch { continue; }
                if (cfg == null || (kv.Key != Plugin.GUID && !OptedIn(cfg))) continue;
                var m = new ModEntry { Guid = kv.Key, Name = info.Metadata.Name, Version = info.Metadata.Version.ToString(), Config = cfg };
                foreach (var e in cfg.Keys)
                {
                    if (string.Equals(e.Key, "Apocasetter", StringComparison.OrdinalIgnoreCase)) continue;
                    m.Entries.Add(cfg[e]);
                }
                list.Add(m);
            }
            _mods = list.OrderBy(m => m.Guid == Plugin.GUID ? 0 : 1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();   // Apocasetter itself always first
            Plugin.Log.LogInfo("Mods menu: " + _mods.Count + " plugin(s) opted in: " + string.Join(", ", _mods.Select(m => m.Name).ToArray()));
            return _mods;
        }

        private static bool OptedIn(ConfigFile cfg)
        {
            foreach (var d in cfg.Keys)
            {
                if (!string.Equals(d.Key, "Apocasetter", StringComparison.OrdinalIgnoreCase)) continue;
                var v = cfg[d].BoxedValue;
                if (v is bool) return (bool)v;
                return string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase);
            }
            try
            {
                if (!string.IsNullOrEmpty(cfg.ConfigFilePath) && File.Exists(cfg.ConfigFilePath))
                    return OptInRx.IsMatch(File.ReadAllText(cfg.ConfigFilePath));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Cannot read " + cfg.ConfigFilePath + ": " + e.Message); }
            return false;
        }
    }

    // =====================================================================
    //  UI (window layout copied from Apocaspawner)
    // =====================================================================
    public class SettingsUI : MonoBehaviour
    {
        private bool _open;
        private Rect _win = new Rect(40, 40, 900, 640);
        private Vector2 _catScroll, _listScroll;
        private string _search = "";
        private string _selected = "";
        private CursorLockMode _prevLock;
        private bool _prevVisible;
        private bool _unblockNextFrame;
        // cursor state seen while the window had focus, re-applied after Alt+Tab (Unity releases the lock on focus loss)
        private CursorLockMode _lastLock = CursorLockMode.None;
        private bool _lastVisible = true;
        private int _relockFrames;

        private readonly Dictionary<ConfigEntryBase, string> _pending = new Dictionary<ConfigEntryBase, string>();
        private readonly Dictionary<ConfigEntryBase, string> _errors = new Dictionary<ConfigEntryBase, string>();
        private ConfigEntryBase _dropdown;
        private Vector2 _dropScroll;
        private string _status = "";
        private float _statusUntil;

        private void Update()
        {
            if (_unblockNextFrame) { _unblockNextFrame = false; if (!_open) InputBlocker.Set(false); }
            if (Plugin.Pressed(Plugin.MenuKeyEntry.Value)) Toggle();
            else if (_open && Plugin.Pressed(Key.Escape)) Toggle();
            if (!_open) GameMenu.Tick(Open);
        }

        private void LateUpdate()
        {
            if (_open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }
            if (_relockFrames > 0)
            {
                _relockFrames--;
                if (InputBlocker.Active) { _relockFrames = 0; return; } // another mod's UI took over meanwhile
                // set None first so Unity treats it as a change even if it still reports the old value
                Cursor.lockState = CursorLockMode.None;
                Cursor.lockState = _lastLock;
                Cursor.visible = _lastVisible;
                if (_relockFrames == 0) Plugin.Log.LogInfo("Cursor re-locked after focus regain (" + _lastLock + ")");
                return;
            }
            if (Application.isFocused && !InputBlocker.Active)
            {
                _lastLock = Cursor.lockState;
                _lastVisible = Cursor.visible;
            }
        }

        private void OnApplicationFocus(bool focus)
        {
            if (!focus) { _relockFrames = 0; return; }
            if (!_open && !InputBlocker.Active && _lastLock != CursorLockMode.None
                && Plugin.RestoreCursorEntry != null && Plugin.RestoreCursorEntry.Value)
                _relockFrames = 3; // apply over a few frames: the lock set in the focus frame itself is often ignored
        }

        public void Open() { if (!_open) Toggle(); }

        private void Toggle()
        {
            _open = !_open;
            if (_open)
            {
                _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
                InputBlocker.Set(true);
                ModCatalog.Invalidate();
                _pending.Clear(); _errors.Clear(); _dropdown = null;
                _win.x = Mathf.Max(0, (Screen.width - _win.width) / 2f);
                _win.y = Mathf.Max(0, (Screen.height - _win.height) / 2f);
            }
            else
            {
                if (_pending.Count > 0) ApplyPending();
                Cursor.lockState = _prevLock; Cursor.visible = _prevVisible;
                _unblockNextFrame = true; // keep blocking one more frame so the closing keypress isn't seen by the game
            }
        }

        private GUIStyle _hintStyle;

        private void OnGUI()
        {
            if (!_open)
            {
                if (!GameMenu.ButtonOnScreen && !GameMenu.HasButton && (GameMenu.MenuVisible || (!GameMenu.InGame && Cursor.visible))) DrawFallbackButton();
                return;
            }
            Theme.Apply();
            _win = GUILayout.Window(0xA95E7, _win, Draw, "", GUILayout.Width(900), GUILayout.Height(640));
        }

        // Used only when no native menu button could be cloned.
        private void DrawFallbackButton()
        {
            Theme.Apply();
            if (_hintStyle == null || _hintStyle.font != GUI.skin.font)
                _hintStyle = new GUIStyle(GUI.skin.button) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, fixedHeight = 0 };
            var r = new Rect(Screen.width - 190, 24, 160, 46);
            if (GUI.Button(r, "MODS", _hintStyle)) Open();
        }

        private void Draw(int id)
        {
            var titleStyle = Theme.Header ?? new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            const float titleY = 8f;
            var titleText = Plugin.NAME + " " + Plugin.VERSION;
            var titleSize = titleStyle.CalcSize(new GUIContent(titleText));
            GUI.Label(new Rect(14, titleY, titleSize.x + 4, titleSize.y + 2), titleText, titleStyle);
            var closeText = "[Esc] Close";
            var closeSize = titleStyle.CalcSize(new GUIContent(closeText));
            if (GUI.Button(new Rect(_win.width - closeSize.x - 14, titleY, closeSize.x + 4, closeSize.y + 2), closeText, titleStyle)) Toggle();

            var mods = ModCatalog.Mods();

            // ---- top bar ----
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(50));
            _search = GUILayout.TextField(_search, GUILayout.Width(260));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            // ---- two panes ----
            GUILayout.BeginHorizontal();

            // left: plugins
            var catStyle = Theme.Category ?? GUI.skin.button;
            GUILayout.BeginVertical(Theme.LeftPanel ?? GUI.skin.box, GUILayout.Width(190));
            _catScroll = GUILayout.BeginScrollView(_catScroll);
            if (mods.Count == 0) GUILayout.Label("No plugin has\nApocasetter = true\nin its config.");
            if (_selected == "" && mods.Count > 0) _selected = mods[0].Guid;
            foreach (var m in mods)
                if (GUILayout.Toggle(_selected == m.Guid, " " + m.Name + " (" + m.Entries.Count + ")", catStyle)) { if (_selected != m.Guid) { _selected = m.Guid; _dropdown = null; _listScroll = Vector2.zero; } }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            // right: settings of the selected plugin
            GUILayout.BeginVertical(Theme.RightPanel ?? GUI.skin.box);
            var mod = mods.FirstOrDefault(m => m.Guid == _selected);
            if (mod != null) DrawMod(mod);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            // ---- bottom bar ----
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            bool dirty = _pending.Count > 0;
            GUILayout.Label(Time.unscaledTime < _statusUntil ? _status : (dirty ? _pending.Count + " unsaved change(s)" : ""), Theme.SubLabel ?? GUI.skin.label);
            GUILayout.FlexibleSpace();
            if (mod != null && GUILayout.Button("Reload from file", GUILayout.Width(130))) { _pending.Clear(); _errors.Clear(); mod.Config.Reload(); Status("Reloaded " + Path.GetFileName(mod.Config.ConfigFilePath)); }
            if (mod != null && GUILayout.Button("Open config folder", GUILayout.Width(140))) { try { Application.OpenURL("file:///" + Paths.ConfigPath.Replace('\\', '/')); } catch (Exception e) { Status(e.Message); } }
            GUI.enabled = dirty;
            if (GUILayout.Button("Save", GUILayout.Width(90))) ApplyPending();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) && _pending.Count > 0)
            { ApplyPending(); Event.current.Use(); }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void Status(string s) { _status = s; _statusUntil = Time.unscaledTime + 3f; }

        private void DrawMod(ModEntry mod)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(mod.Name + " " + mod.Version, Theme.Header ?? GUI.skin.label);
            GUILayout.FlexibleSpace();
            GUILayout.Label(Path.GetFileName(mod.Config.ConfigFilePath ?? ""), Theme.SubLabel ?? GUI.skin.label);
            GUILayout.EndHorizontal();

            _listScroll = GUILayout.BeginScrollView(_listScroll);
            var s = _search.Trim().ToLowerInvariant();
            int shown = 0;
            foreach (var section in mod.Entries.GroupBy(e => e.Definition.Section))
            {
                var rows = section.Where(e => s.Length == 0 || e.Definition.Key.ToLowerInvariant().Contains(s)
                                          || (e.Description != null && e.Description.Description != null && e.Description.Description.ToLowerInvariant().Contains(s))
                                          || section.Key.ToLowerInvariant().Contains(s)).ToList();
                if (rows.Count == 0) continue;
                GUILayout.Space(6);
                GUILayout.Label("[" + section.Key + "]", Theme.Header ?? GUI.skin.label);
                foreach (var e in rows) { DrawEntry(e); shown++; }
            }
            if (shown == 0) GUILayout.Label("Nothing matches.");
            GUILayout.EndScrollView();
        }

        private static readonly Type[] NumericTypes = { typeof(int), typeof(float), typeof(double), typeof(long), typeof(short), typeof(byte), typeof(decimal) };

        private void DrawEntry(ConfigEntryBase e)
        {
            var t = e.SettingType;
            string pending;
            bool isPending = _pending.TryGetValue(e, out pending);
            string err; _errors.TryGetValue(e, out err);

            GUILayout.BeginHorizontal();
            GUILayout.Label((isPending ? "* " : "") + e.Definition.Key, GUILayout.Width(200));

            if (t == typeof(bool))
            {
                bool v = (bool)e.BoxedValue;
                bool nv = GUILayout.Toggle(v, v ? " On" : " Off", GUILayout.Width(80));
                if (nv != v) SetNow(e, nv);
            }
            else if (t.IsEnum || AcceptableList(e) != null)
            {
                var options = t.IsEnum ? Enum.GetNames(t) : AcceptableList(e).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToArray();
                var cur = e.GetSerializedValue();
                if (GUILayout.Button(cur + "  ▼", GUILayout.Width(220))) { _dropdown = _dropdown == e ? null : e; _dropScroll = Vector2.zero; }
                if (GUILayout.Button("<", GUILayout.Width(26))) Cycle(e, options, cur, -1);
                if (GUILayout.Button(">", GUILayout.Width(26))) Cycle(e, options, cur, +1);
            }
            else
            {
                object min = null, max = null;
                bool ranged = NumericTypes.Contains(t) && Range(e, out min, out max);
                if (ranged)
                {
                    float fmin = Convert.ToSingle(min), fmax = Convert.ToSingle(max);
                    float fv = isPending ? ParseF(pending, Convert.ToSingle(e.BoxedValue)) : Convert.ToSingle(e.BoxedValue);
                    float nfv = GUILayout.HorizontalSlider(fv, fmin, fmax, GUILayout.Width(150));
                    if (Math.Abs(nfv - fv) > 1e-6f)
                    {
                        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) _pending[e] = nfv.ToString("0.###", CultureInfo.InvariantCulture);
                        else _pending[e] = Mathf.RoundToInt(nfv).ToString();
                    }
                    GUILayout.Space(6);
                }
                var text = isPending ? pending : e.GetSerializedValue();
                var nt = GUILayout.TextField(text, GUILayout.Width(ranged ? 70 : 240));
                if (nt != text) { if (nt == e.GetSerializedValue()) _pending.Remove(e); else _pending[e] = nt; }
            }

            GUILayout.FlexibleSpace();
            var def = e.DefaultValue;
            GUI.enabled = def != null && !Equals(def, e.BoxedValue);
            if (GUILayout.Button("Reset", GUILayout.Width(60))) { _pending.Remove(e); SetNow(e, def); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // dropdown list
            if (_dropdown == e)
            {
                var options = t.IsEnum ? Enum.GetNames(t) : AcceptableList(e).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToArray();
                GUILayout.BeginHorizontal();
                GUILayout.Space(204);
                GUILayout.BeginVertical(Theme.LeftPanel ?? GUI.skin.box, GUILayout.Width(300));
                _dropScroll = GUILayout.BeginScrollView(_dropScroll, GUILayout.Height(Mathf.Min(220, options.Length * (Theme.Spec.categoryHeight + 4) + 8)));
                var cur = e.GetSerializedValue();
                foreach (var o in options)
                    if (GUILayout.Toggle(o == cur, " " + o, Theme.Category ?? GUI.skin.button) && o != cur) { SetSerialized(e, o); _dropdown = null; }
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }

            // description / error
            var desc = e.Description != null ? e.Description.Description : null;
            if (!string.IsNullOrEmpty(err)) GUILayout.Label("  ! " + err, Theme.SubLabel ?? GUI.skin.label);
            else if (!string.IsNullOrEmpty(desc)) GUILayout.Label("  " + desc + RangeText(e), Theme.SubLabel ?? GUI.skin.label);
            GUILayout.Space(2);
        }

        private static float ParseF(string s, float fb) { float f; return s != null && float.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fb; }

        private void Cycle(ConfigEntryBase e, string[] options, string cur, int dir)
        {
            if (options.Length == 0) return;
            int i = Array.IndexOf(options, cur);
            i = i < 0 ? 0 : (i + dir + options.Length) % options.Length;
            SetSerialized(e, options[i]);
        }

        private static string RangeText(ConfigEntryBase e)
        {
            object min, max;
            if (Range(e, out min, out max)) return "  (" + Convert.ToString(min, CultureInfo.InvariantCulture) + " .. " + Convert.ToString(max, CultureInfo.InvariantCulture) + ")";
            return "";
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
            int ok = 0, bad = 0;
            foreach (var kv in _pending.ToList())
            {
                try { kv.Key.SetSerializedValue(Normalize(kv.Key, kv.Value)); _errors.Remove(kv.Key); _pending.Remove(kv.Key); ok++; }
                catch (Exception ex) { _errors[kv.Key] = ex.Message; bad++; }
            }
            foreach (var m in ModCatalog.Mods()) SaveFile(m.Config);
            Status(bad == 0 ? "Saved " + ok + " setting(s)" : ok + " saved, " + bad + " invalid");
        }

        private void Persist(ConfigEntryBase e)
        {
            SaveFile(e.ConfigFile);
            Status("Saved " + e.Definition.Key);
        }

        private static void SaveFile(ConfigFile cfg)
        {
            // SaveOnConfigSet normally writes on every set; force it in case a plugin turned that off
            try { cfg.Save(); } catch (Exception ex) { Plugin.Log.LogWarning("Config save failed: " + ex.Message); }
        }
    }
}
