using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Apocasetter
{
    // =====================================================================
    //  Apocasetter: a "MODS" button in the main menu / pause menu that opens
    //  an in-game editor for every BepInEx plugin config that opts in with
    //  a line  "Apocasetter = true"  anywhere in its .cfg file.
    //  Changes are applied to the live ConfigEntry (so mods that read
    //  entry.Value pick them up instantly) and written to disk.
    // =====================================================================
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocasetter";
        public const string NAME = "Apocasetter";
        public const string VERSION = "2.0.2";

        public static ManualLogSource Log;
        public static string PluginPath;
        public static ConfigEntry<Key> MenuKeyEntry;
        public static ConfigEntry<bool> RestoreCursorEntry;
        public static ConfigEntry<bool> CheckUpdatesEntry, UpdateNoticeEntry;
        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;
            PluginPath = Info.Location;
            MenuKeyEntry = Config.Bind("Keys", "ToggleMenu", Key.F6, "Hotkey that opens/closes the Mods window from anywhere (the MODS menu button always works)");
            RestoreCursorEntry = Config.Bind("General", "RestoreCursorAfterAltTab", true,
                "Re-lock and hide the mouse cursor when the game window regains focus (Alt+Tab) if it was locked before. Unity drops the lock on focus loss and the game only sets it on menu transitions, so the cursor otherwise stays on screen.");

            CheckUpdatesEntry = Config.Bind("Updates", "CheckForUpdates", true,
                "Once per game start (results kept for 6 hours), download the Apocasetter index from GitHub and compare it with your installed mods. Nothing about you or your PC is sent.");
            UpdateNoticeEntry = Config.Bind("Updates", "UpdateNotice", true,
                "Show a badge on the MODS button and a short message on the title screen when an update is found.");

            SceneManager.sceneLoaded += (s, m) => EnsureRunner("scene " + s.name);
            InputBlocker.Install();
            Theme.Load(Info.Location);
            GameSkin.Init(Info.Location);
            Updates.Init();
            EnsureRunner("Awake");
            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }

        // The game destroys the BepInEx plugin object on scene load; keep our logic on a hidden object it can't find.
        private static void EnsureRunner(string why)
        {
            if (_runner != null && _runner.activeInHierarchy) return;
            _runner = new GameObject("Apocasetter.Runner") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_runner);
            _runner.AddComponent<SettingsUI>();
            Log.LogInfo("Runner created (" + why + ")");
        }

        public static bool Pressed(Key key)
        {
            if (key == Key.None) return false;
            try { var kb = Keyboard.current; if (kb != null) return kb[key].wasPressedThisFrame; } catch { }
            try { KeyCode kc; if (Enum.TryParse(key.ToString(), out kc)) return Input.GetKeyDown(kc); } catch { }
            return false;
        }
    }

    // =====================================================================
    //  Input blocking while the window is open (same recipe as Apocaspawner).
    // =====================================================================
    public static class InputBlocker
    {
        public static bool Active;
        private static readonly Regex ActionRx = new Regex(
            @"^(GetAxis|GetButton|GetKey|GetMouse|MouseLook|MousePick|AnyKey|GetTouch|GetAxisKeyAxis|Input|Mouse)",
            RegexOptions.IgnoreCase);
        private static float _savedTimeScale = 1f;

        public static void Install()
        {
            var harmony = new Harmony(Plugin.GUID);
            var prefix = new HarmonyMethod(typeof(InputBlocker).GetMethod("SkipWhenActive", BindingFlags.Static | BindingFlags.NonPublic));
            int patched = 0;
            var assemblies = new[] { typeof(PlayMakerArrayListProxy).Assembly, typeof(PlayMakerFSM).Assembly };
            foreach (var asm in assemblies)
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                {
                    if (t == null || !typeof(FsmStateAction).IsAssignableFrom(t) || t.IsAbstract) continue;
                    if (!ActionRx.IsMatch(t.Name)) continue;
                    foreach (var mName in new[] { "OnUpdate", "OnFixedUpdate", "OnLateUpdate" })
                    {
                        var m = t.GetMethod(mName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (m == null) continue;
                        try { harmony.Patch(m, prefix: prefix); patched++; }
                        catch (Exception e) { Plugin.Log.LogWarning("Could not patch " + t.Name + "." + mName + ": " + e.Message); }
                    }
                }
            }
            Plugin.Log.LogInfo("Input blocker: patched " + patched + " PlayMaker input action methods");
        }

        private static bool SkipWhenActive() { return !Active; }

        public static void Set(bool on)
        {
            if (on == Active) return;
            Active = on;
            if (on) { _savedTimeScale = Time.timeScale; Time.timeScale = 0f; }
            else { Time.timeScale = _savedTimeScale <= 0f ? 1f : _savedTimeScale; }
        }
    }

    // =====================================================================
    //  Game menu helpers + the native-looking "MODS" button
    // =====================================================================
    public static class GameMenu
    {
        private static PlayMakerFSM _menuFsm;
        private static GameObject _menuRoot;
        private static float _nextSearch;

        public static bool InGame { get { return GameObject.Find("__GameManager__") != null; } }

        /// true while the game's own ESC pause menu is up
        public static bool Paused
        {
            get
            {
                if (_menuFsm == null)
                {
                    var gm = GameObject.Find("__GameManager__");
                    if (gm == null) return false;
                    _menuFsm = gm.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Menu");
                }
                return _menuFsm != null && _menuFsm.ActiveStateName == "pause";
            }
        }

        /// MainMenu_Canvas/MainMenu (title screen and pause menu share the layout)
        public static GameObject MenuRoot
        {
            get
            {
                if (_menuRoot != null) return _menuRoot;
                if (Time.unscaledTime < _nextSearch) return null;
                _nextSearch = Time.unscaledTime + 0.5f;
                var canvas = GameObject.Find("MainMenu_Canvas");
                if (canvas == null)
                {
                    // inactive canvases are invisible to GameObject.Find
                    canvas = Resources.FindObjectsOfTypeAll<Canvas>().Select(c => c.gameObject)
                        .FirstOrDefault(g => g.scene.IsValid() && g.name == "MainMenu_Canvas");
                }
                if (canvas == null) return null;
                var t = canvas.transform.Find("MainMenu");
                _menuRoot = t != null ? t.gameObject : canvas;
                return _menuRoot;
            }
        }

        public static bool MenuVisible
        {
            get
            {
                var root = MenuRoot;
                if (root == null) return false;
                if (!root.activeInHierarchy) return false;
                var cg = root.GetComponentInParent<CanvasGroup>();
                if (cg != null && cg.alpha <= 0.01f) return false;
                if (InGame && !Paused) return false;
                return true;
            }
        }

        public static void Invalidate() { _menuFsm = null; _menuRoot = null; }

        // ---------------------------------------------------------------- MODS button
        // The game has more than one menu canvas (the title screen and the ESC menu are different objects that
        // both contain a "Settings" button), so every enabled canvas that shows game menu buttons gets its own clone.
        private static readonly string[] TemplateNames = { "Settings", "Credits", "Tutorial", "Codex", "Quit", "Quit_To_Menu", "Exit", "Options" };
        private class Slot { public Canvas Canvas; public GameObject Button; public Button[] GameButtons = new Button[0]; public bool Shown; }
        private static readonly List<Slot> _slots = new List<Slot>();
        private static float _nextScan;
        private static bool _loggedCanvases;

        public static bool HasButton { get { return _slots.Any(x => x.Button != null); } }

        private static bool IsMenuButton(Button b, GameObject ours)
        {
            if (b == null || !b.isActiveAndEnabled) return false;
            if (ours != null && (b.gameObject == ours || b.transform.IsChildOf(ours.transform))) return false;
            var n = b.gameObject.name;
            return TemplateNames.Any(t => string.Equals(t, n, StringComparison.OrdinalIgnoreCase));
        }

        /// Called every frame from the runner.
        public static void Tick(Action onClick)
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 0.25f;
            _slots.RemoveAll(x => x.Canvas == null);

            // find every enabled canvas that currently shows a game menu button
            Canvas[] canvases;
            try { canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(); } catch { return; }
            if (!_loggedCanvases)
            {
                _loggedCanvases = true;
                var sb = new System.Text.StringBuilder("Canvases at first scan:\n");
                foreach (var c in canvases)
                    sb.AppendLine("   " + Path(c.transform) + " enabled=" + c.enabled + " mode=" + c.renderMode + " scale=" + c.transform.lossyScale.x.ToString("0.##")
                        + " buttons=" + string.Join(",", c.GetComponentsInChildren<Button>(false).Select(x => x.gameObject.name).ToArray()));
                Plugin.Log.LogInfo(sb.ToString());
            }
            foreach (var c in canvases)
            {
                if (c == null || !c.enabled || !c.isActiveAndEnabled || c.transform.lossyScale.x <= 0.0001f) continue;
                var slot = _slots.FirstOrDefault(x => x.Canvas == c);
                if (slot == null)
                {
                    var buttons = c.GetComponentsInChildren<Button>(true);
                    if (!buttons.Any(x => IsMenuButton(x, null))) continue;
                    slot = new Slot { Canvas = c };
                    _slots.Add(slot);
                    Inject(slot, onClick);
                }
                slot.GameButtons = c.GetComponentsInChildren<Button>(true);
            }

            // show/hide each clone with its canvas
            foreach (var slot in _slots)
            {
                if (slot.Button == null) continue;
                var c = slot.Canvas;
                bool vis = c.enabled && c.isActiveAndEnabled && c.transform.lossyScale.x > 0.0001f && slot.GameButtons.Any(x => IsMenuButton(x, slot.Button));
                if (vis != slot.Button.activeSelf) slot.Button.SetActive(vis);
                if (vis && !slot.Shown) { slot.Button.transform.SetAsLastSibling(); Plugin.Log.LogInfo("MODS button shown on " + Path(c.transform) + " " + Diag(slot)); }
                slot.Shown = vis;
            }
        }

        private static string Path(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }

        private static void Inject(Slot slot, Action onClick)
        {
            var canvas = slot.Canvas.gameObject;
            try
            {
                var template = canvas.GetComponentsInChildren<Button>(true)
                    .OrderBy(b => { var i = Array.FindIndex(TemplateNames, n => string.Equals(n, b.gameObject.name, StringComparison.OrdinalIgnoreCase)); return i < 0 ? 99 : i; })
                    .FirstOrDefault();
                if (template == null) { Plugin.Log.LogWarning("MODS button: no template button under " + canvas.name); return; }

                var go = UnityEngine.Object.Instantiate(template.gameObject, canvas.transform);
                go.name = "Apocasetter_Mods";
                // strip the game's logic (PlayMaker FSMs, nested confirm dialogs, arrows...)
                foreach (var f in go.GetComponentsInChildren<PlayMakerFSM>(true)) UnityEngine.Object.DestroyImmediate(f);
                foreach (var f in go.GetComponentsInChildren<PlayMakerProxyBase>(true)) UnityEngine.Object.DestroyImmediate(f);
                for (int i = go.transform.childCount - 1; i >= 0; i--)
                {
                    var c = go.transform.GetChild(i);
                    bool isText = c.GetComponentInChildren<Text>(true) != null || c.GetComponentInChildren<TMPro.TMP_Text>(true) != null;
                    bool isGraphic = c.GetComponent<Graphic>() != null;
                    if (!isText && !isGraphic) UnityEngine.Object.DestroyImmediate(c.gameObject);
                    else c.gameObject.SetActive(true);
                }
                foreach (var tx in go.GetComponentsInChildren<Text>(true))
                {
                    tx.text = Caps(tx.text) ? "MODS" : "Mods";
                    if (tx.font != null) GameSkin.SetGameFont(tx.font);
                }
                foreach (var tx in go.GetComponentsInChildren<TMPro.TMP_Text>(true))
                    if (tx.font != null && tx.font.sourceFontFile != null) GameSkin.SetGameFont(tx.font.sourceFontFile);
                try
                {
                    var img = template.GetComponent<Image>();
                    Plugin.Log.LogInfo("MODS button template: sprite '" + (img != null && img.sprite != null ? img.sprite.name : "-") + "', font '"
                        + string.Join(",", go.GetComponentsInChildren<Text>(true).Select(t => t.font != null ? t.font.name : "-").ToArray()) + "' / TMP '"
                        + string.Join(",", go.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t => t.font != null ? t.font.name : "-").ToArray()) + "'");
                }
                catch { }
                foreach (var tx in go.GetComponentsInChildren<TMPro.TMP_Text>(true)) tx.text = Caps(tx.text) ? "MODS" : "Mods";

                var btn = go.GetComponentInChildren<Button>(true);
                btn.onClick = new Button.ButtonClickedEvent();
                btn.onClick.AddListener(() => onClick());
                btn.interactable = true;
                var cg = go.GetComponent<CanvasGroup>(); if (cg != null) { cg.alpha = 1; cg.interactable = true; cg.blocksRaycasts = true; }

                // ignore any layout group and pin to the top-right corner of the canvas
                var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
                var rt = go.GetComponent<RectTransform>();
                var trt = template.GetComponent<RectTransform>();
                var size = trt != null ? trt.rect.size : new Vector2(200, 50);
                if (size.x < 10 || size.y < 10) size = new Vector2(300, 55);
                rt.anchorMin = new Vector2(1, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(1, 1);
                rt.sizeDelta = size;
                rt.anchoredPosition = new Vector2(-30, -30);
                rt.localScale = Vector3.one;
                go.transform.SetAsLastSibling();
                go.SetActive(false);

                slot.Button = go;
                Plugin.Log.LogInfo("MODS button injected under " + Path(canvas.transform) + " (template '" + template.gameObject.name + "', " + (int)size.x + "x" + (int)size.y + ")");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("MODS button injection failed on " + canvas.name + ": " + e);
                if (slot.Button != null) UnityEngine.Object.Destroy(slot.Button);
                slot.Button = null;
            }
        }

        /// IMGUI rects (top-left origin) of every MODS button currently on screen.
        public static List<Rect> VisibleButtonRects()
        {
            var list = new List<Rect>();
            foreach (var slot in _slots)
            {
                if (slot.Button == null || !slot.Button.activeInHierarchy) continue;
                var c = slot.Canvas;
                if (c == null || !c.enabled || !c.isActiveAndEnabled) continue;
                var r = ScreenRect(slot.Button.GetComponent<RectTransform>(), c);
                if (r.width < 2 || r.height < 2 || r.xMax <= 0 || r.yMax <= 0 || r.xMin >= Screen.width || r.yMin >= Screen.height) continue;
                list.Add(new Rect(r.x, Screen.height - r.yMax, r.width, r.height));
            }
            return list;
        }

        /// True when a native MODS button is currently drawn somewhere inside the screen.
        public static bool ButtonOnScreen
        {
            get
            {
                foreach (var slot in _slots)
                {
                    if (slot.Button == null || !slot.Button.activeInHierarchy) continue;
                    var c = slot.Canvas;
                    if (c == null || !c.enabled || !c.isActiveAndEnabled) continue;
                    var r = ScreenRect(slot.Button.GetComponent<RectTransform>(), c);
                    if (r.xMax > 0 && r.yMax > 0 && r.xMin < Screen.width && r.yMin < Screen.height && r.width > 1 && r.height > 1) return true;
                }
                return false;
            }
        }

        private static Rect ScreenRect(RectTransform rt, Canvas c)
        {
            var corners = new Vector3[4]; rt.GetWorldCorners(corners);
            var cam = c.renderMode == RenderMode.ScreenSpaceOverlay ? null : c.worldCamera;
            var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var w in corners)
            {
                Vector2 p = cam != null ? (Vector2)cam.WorldToScreenPoint(w) : (Vector2)w;
                min = Vector2.Min(min, p); max = Vector2.Max(max, p);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static string Diag(Slot slot)
        {
            try
            {
                var c = slot.Canvas;
                var crt = c.GetComponent<RectTransform>();
                return "canvas=" + c.renderMode + " sort=" + c.sortingOrder + " rect=" + (int)crt.rect.width + "x" + (int)crt.rect.height
                     + " scale=" + crt.lossyScale.x.ToString("0.###") + " button=" + ScreenRect(slot.Button.GetComponent<RectTransform>(), c)
                     + " screen=" + Screen.width + "x" + Screen.height;
            }
            catch (Exception e) { return "diag failed: " + e.Message; }
        }

        private static bool Caps(string s) { return string.IsNullOrEmpty(s) || s == s.ToUpperInvariant(); }
    }
}
