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
using UnityEngine.EventSystems;
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
        public const string VERSION = "2.1.2";

        public static ManualLogSource Log;
        public static string PluginPath;
        public static ConfigEntry<Key> MenuKeyEntry;
        public static ConfigEntry<bool> RestoreCursorEntry;
        public static ConfigEntry<bool> CheckUpdatesEntry, UpdateNoticeEntry, VerboseLogEntry;
        public static ConfigEntry<int> UpdateFrequencyEntry;
        public static bool IsVerbose { get { return VerboseLogEntry != null && VerboseLogEntry.Value; } }
        public static void Verbose(string msg) { if (IsVerbose) Log.LogInfo("[verbose] " + msg); }
        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;
            PluginPath = Info.Location;
            MenuKeyEntry = Config.Bind("Keys", "ToggleMenu", Key.F6, "Hotkey that opens/closes the Mods window from anywhere (the MODS menu button always works)");
            RestoreCursorEntry = Config.Bind("General", "RestoreCursorAfterAltTab", true,
                "Re-lock and hide the mouse cursor when the game window regains focus (Alt+Tab) if it was locked before. Unity drops the lock on focus loss and the game only sets it on menu transitions, so the cursor otherwise stays on screen.");

            CheckUpdatesEntry = Config.Bind("Updates", "CheckForUpdates", true,
                "Check for mod updates at game start, reusing cached results according to UpdateFrequency. Nothing about you or your PC is sent.");
            UpdateFrequencyEntry = Config.Bind("Updates", "UpdateFrequency", 6, new ConfigDescription(
                "How long to reuse update results, in hours (0-48). 0 = Each time: check on every game start. The default is 6 hours. CHECK FOR UPDATES always checks immediately.",
                new AcceptableValueRange<int>(0, 48)));
            UpdateNoticeEntry = Config.Bind("Updates", "UpdateNotice", true,
                "Show a badge on the MODS button and a short message on the title screen when an update is found.");
            VerboseLogEntry = Config.Bind("Debug", "VerboseLog", false,
                "Write extra lines to BepInEx\\LogOutput.log while the Mods window is open: every click and whether the window received it, the mouse cursor state, screen size and scaling, and which other mods draw on screen. Turn it on only when you are asked for a log, then send LogOutput.log.");

            SceneManager.sceneLoaded += (s, m) => { GameMenu.Invalidate(); EnsureRunner("scene " + s.name); };
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
            // Guard dispatch itself: disabling an EventSystem alone leaves a race with scene loads,
            // other mods re-enabling it, or an input module already processing this frame.
            PatchUi(harmony, typeof(EventSystem), "Update", "BeforeGameUiUpdate");
            PatchUi(harmony, typeof(Button), "Press", "SkipWhenActive");
            PatchUi(harmony, typeof(PlayMakerUiEventBase), "SendEvent", "SkipWhenActive");
            PatchUi(harmony, typeof(HutongGames.PlayMaker.Actions.UiButtonOnClickEvent), "DoOnClick", "SkipWhenActive");
        }

        private static void PatchUi(Harmony harmony, Type type, string method, string guard)
        {
            var target = AccessTools.DeclaredMethod(type, method);
            if (target == null) throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(InputBlocker), guard));
        }

        private static bool BeforeGameUiUpdate()
        {
            // Poll before UGUI dispatch so the opening press cannot also click a menu underneath.
            // This path and the runner's Update never depend on IMGUI's Event.current.
            GameMenu.PollClick();
            return !Active;
        }

        private static bool SkipWhenActive() { return !Active; }

        public static void Set(bool on)
        {
            if (on == Active) return;
            Active = on;
            if (on) { _savedTimeScale = Time.timeScale; Time.timeScale = 0f; }
            else { Time.timeScale = _savedTimeScale; }
        }
    }

    // =====================================================================
    //  Game menu helpers + the native-looking "MODS" button
    // =====================================================================
    public static partial class GameMenu
    {
        private static PlayMakerFSM _menuFsm;
        private static GameObject _menuRoot;
        private static float _nextSearch;
        private static GameObject _gameManager;
        private static bool _searchedGameManager;

        public static bool InGame
        {
            get
            {
                if (!_searchedGameManager) { _gameManager = GameObject.Find("__GameManager__"); _searchedGameManager = true; }
                return _gameManager != null;
            }
        }

        /// true while the game's own ESC pause menu is up
        public static bool Paused
        {
            get
            {
                if (_menuFsm == null)
                {
                    var gm = InGame ? _gameManager : null;
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

        public static void Invalidate()
        {
            _menuFsm = null; _menuRoot = null; _searchedGameManager = false;
            _nextSearch = 0; _nextScan = 0; _rectFrame = -1;
        }

    }
}
