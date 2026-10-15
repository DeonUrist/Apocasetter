using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Apocasetter;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace ApocasetterVerification
{
    // Opt-in only. The runner exits the game after testing the title screen; it never loads a save.
    [BepInPlugin("com.apocasetter.verification", "Apocasetter verification", "1.0.0")]
    [BepInDependency(Apocasetter.Plugin.GUID)]
    public class ProbePlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            if (!Environment.GetCommandLineArgs().Contains("--apocasetter-verify")) return;
            var go = new GameObject("Apocasetter.Verification") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<Probe>();
        }
    }

    public class Probe : MonoBehaviour
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly HashSet<string> Drawn = new HashSet<string>();
        private static bool EatGui;
        private readonly List<string> _results = new List<string>();
        private string _output;
        private SettingsUI _ui;
        private Mouse _mouse;

        private IEnumerator Start()
        {
            _output = Environment.GetCommandLineArgs().First(a => a.StartsWith("--apocasetter-verify-output=")).Substring("--apocasetter-verify-output=".Length);
            _results.Add("Apocasetter runtime verification " + DateTime.UtcNow.ToString("u"));
            var run = Run();
            while (true)
            {
                bool more;
                try { more = run.MoveNext(); }
                catch (Exception e) { _results.Add("FAIL " + e); break; }
                if (!more) break;
                yield return run.Current;
            }
            File.WriteAllLines(_output, _results.ToArray());
            Debug.Log("Apocasetter verification: " + _results.Last());
            Application.Quit();
        }

        private void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(label);
            _results.Add("PASS " + label);
            File.WriteAllLines(_output, _results.ToArray());
        }
        private static T Get<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Flags).GetValue(owner); }
        private static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Flags).SetValue(owner, value); }
        private static object Call(object owner, string name, params object[] args) { return owner.GetType().GetMethod(name, Flags).Invoke(owner, args); }
        private static void EatEvent()
        {
            if (!EatGui) return;
            if (Event.current.isMouse || Event.current.isKey || Event.current.type == EventType.ScrollWheel) Event.current.Use();
            GUI.enabled = false; GUI.color = Color.clear;
        }
        private static void Entry(BepInEx.Configuration.ConfigEntryBase e) { Drawn.Add(e.Definition.Section + "/" + e.Definition.Key); }
        private void MouseStateAt(Vector2 position, bool pressed)
        {
            InputSystem.QueueStateEvent(_mouse, new MouseState { position = position, buttons = (ushort)(pressed ? 1 : 0) });
        }

        private IEnumerator Run()
        {
            Application.runInBackground = true;
            float deadline = Time.realtimeSinceStartup + 60;
            while (SettingsUI.Instance == null || !GameMenu.ButtonOnScreen || Time.realtimeSinceStartup < 6)
            {
                if (Time.realtimeSinceStartup > deadline) throw new Exception("No visible MODS button within 60 seconds");
                yield return null;
            }
            _ui = SettingsUI.Instance;
            Check(GameMenu.ButtonOnScreen, "Native MODS button appears on title screen");
            Check(Application.isFocused, "Game has focus for raw mouse verification");
            var button = GameObject.Find("Canvas_MM/Apocasetter_Mods");
            Check(button != null, "Button injected into title menu canvas");
            Check(button.GetComponentsInChildren<PlayMakerFSM>(true).Length == 0 && button.GetComponent<Button>().onClick.GetPersistentEventCount() == 0,
                "Injected button has no copied game FSMs or persistent callbacks");
            var harmony = new Harmony("com.apocasetter.verification");
            harmony.Patch(AccessTools.Method(typeof(SettingsUI), "OnGUI"), prefix: new HarmonyMethod(typeof(Probe), "EatEvent"));
            harmony.Patch(AccessTools.Method(typeof(SettingsUI), "DrawEntry"), prefix: new HarmonyMethod(typeof(Probe), "Entry"));
            // A synthetic device avoids changing the user's physical mouse state.
            var physicalMouse = Mouse.current;
            _mouse = InputSystem.AddDevice<Mouse>();
            _mouse.MakeCurrent();
            var rect = GameMenu.VisibleButtonRects()[0];
            var point = new Vector2(rect.center.x, Screen.height - rect.center.y);
            EatGui = true;
            var mouseTrace = new List<string>();
            Action trace = () =>
            {
                // Model a mod switching Mouse.current after a different device's press.
                if (physicalMouse != null) physicalMouse.MakeCurrent();
                mouseTrace.Add("mouse=" + (Mouse.current != null ? Mouse.current.deviceId.ToString() : "none") + ", synthetic=" + _mouse.deviceId + ", pressed=" + _mouse.leftButton.wasPressedThisFrame + ", focus=" + Application.isFocused + ", frame=" + Time.frameCount + ", phase=" + InputState.currentUpdateType);
            };
            InputSystem.onAfterUpdate += trace;
            MouseStateAt(point, true);
            yield return null; yield return null;
            InputSystem.onAfterUpdate -= trace;
            if (!InputBlocker.Active || !Get<bool>(_ui, "_open")) _results.AddRange(mouseTrace);
            Check(InputBlocker.Active && Get<bool>(_ui, "_open"), "Raw mouse opens MODS despite consumed IMGUI events, disabled GUI state and another current mouse");
            MouseStateAt(point, false);
            EatGui = false;
            Set(_ui, "_selected", Apocasetter.Plugin.GUID); Set(_ui, "_tab", "settings");
            yield return null; yield return null;
            var sections = Get<List<string>>(_ui, "_sections");
            Check(sections != null && sections.Contains("General") && sections.Contains("Debug") && sections.Contains("Keys") && sections.Contains("Updates"), "Settings expose all config sections as categories");
            Check(Get<string>(_ui, "_section") == "General", "General is the default category");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(_output), "general.png"));
            Set(_ui, "_section", "Debug"); Drawn.Clear();
            yield return null; yield return null;
            Check(Drawn.Contains("Debug/VerboseLog") && Drawn.All(e => e.StartsWith("Debug/")), "Debug settings render directly with no collapsing; other categories stay out of the pane");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(_output), "debug.png"));
            Set(_ui, "_settingSearch", "cursor"); Drawn.Clear();
            yield return null; yield return null;
            Check(Drawn.Any(e => e.StartsWith("General/")), "Settings search reaches other categories");
            Check(Get<int>(_ui, "_drawErrors") == 0, "Settings tabs and search render without IMGUI exceptions");

            var target = new GameObject("VerificationButton", typeof(RectTransform), typeof(Image), typeof(Button));
            int clicks = 0; target.GetComponent<Button>().onClick.AddListener(() => clicks++);
            AccessTools.Method(typeof(Button), "Press").Invoke(target.GetComponent<Button>(), null);
            Check(clicks == 0, "UGUI button dispatch is blocked while MODS is open");
            var esObject = new GameObject("VerificationEventSystem", typeof(EventSystem), typeof(CountingModule));
            var es = esObject.GetComponent<EventSystem>();
            AccessTools.Method(typeof(EventSystem), "Update").Invoke(es, null);
            Check(CountingModule.Updates == 0, "A newly created enabled EventSystem cannot process input through MODS");

            MouseStateAt(point, true); yield return null; yield return null;
            Call(_ui, "Toggle");
            for (int i = 0; i < 5; i++) yield return null;
            Check(InputBlocker.Active, "Closing while a mouse button is held keeps game input blocked");
            Check(Cursor.lockState == CursorLockMode.None && Cursor.visible, "Closing to the title menu leaves the mouse free immediately");
            MouseStateAt(point, false); yield return null;
            Check(InputBlocker.Active, "The release frame cannot click through into the title menu");
            for (int i = 0; i < 4; i++) yield return null;
            Check(!InputBlocker.Active, "Input resumes promptly after the closing input is released");
            Check(Time.timeScale == 1f, "Unpaused time scale is restored");
            AccessTools.Method(typeof(Button), "Press").Invoke(target.GetComponent<Button>(), null);
            Check(clicks == 1, "UGUI buttons work again after closing MODS");
            Destroy(esObject); Destroy(target);

            Time.timeScale = 0;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
            _ui.Open(); Call(_ui, "Toggle");
            for (int i = 0; i < 4; i++) yield return null;
            Check(Time.timeScale == 0, "An already paused game stays paused after MODS closes");
            Check(Cursor.lockState == CursorLockMode.None && Cursor.visible, "A stale menu cursor lock is not restored on close");
            Time.timeScale = 1;
            Destroy(button); yield return null; yield return null; yield return null;
            button = GameObject.Find("Canvas_MM/Apocasetter_Mods");
            Check(button != null, "A MODS button deleted by another mod is rebuilt");
            Check(FindObjectsOfType<Button>().Count(b => b.name == "Apocasetter_Mods" && b.transform.parent.name == "Canvas_MM") == 1, "Reinjection leaves exactly one native button per menu");
            var originalText = GameObject.Find("Canvas_MM").GetComponentsInChildren<Button>(true)
                .Where(b => b.name == "Quit" || b.name == "Settings").Select(b => b.GetComponentInChildren<Text>(true)).FirstOrDefault(t => t != null);
            Check(originalText != null && GameSkin.GameFont == originalText.font, "Translated button reinjection keeps the IMGUI window on the source legacy font");

            // Model the pause/play state and canvas contracts documented in Sources, without loading a save.
            var titleCanvas = GameObject.Find("Canvas_MM").GetComponent<Canvas>();
            titleCanvas.enabled = false;
            var gm = new GameObject("__GameManager__"); gm.SetActive(false);
            var menuFsm = gm.AddComponent<PlayMakerFSM>(); menuFsm.Reset(); menuFsm.enabled = false; menuFsm.FsmName = "Menu";
            menuFsm.Fsm.States = new[] { new HutongGames.PlayMaker.FsmState(menuFsm.Fsm) { Name = "pause" }, new HutongGames.PlayMaker.FsmState(menuFsm.Fsm) { Name = "play" } };
            gm.SetActive(true);
            AccessTools.Property(menuFsm.Fsm.GetType(), "ActiveState").SetValue(menuFsm.Fsm, menuFsm.Fsm.States[0], null);
            var pauseRoot = new GameObject("MainMenu_Canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            pauseRoot.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var pauseSettings = new GameObject("Settings", typeof(RectTransform), typeof(Image), typeof(Button));
            pauseSettings.transform.SetParent(pauseRoot.transform, false);
            pauseSettings.GetComponent<RectTransform>().sizeDelta = new Vector2(200, 50);
            pauseSettings.GetComponent<Button>().targetGraphic = pauseSettings.GetComponent<Image>();
            GameMenu.Invalidate(); Time.timeScale = 0;
            for (int i = 0; i < 3; i++) yield return null;
            Check(GameMenu.InGame && GameMenu.Paused, "Pause-menu fixture is recognized through the game's Menu FSM state");
            Check(GameObject.Find("MainMenu_Canvas/Apocasetter_Mods") != null, "MODS button is injected into the pause-menu canvas");
            rect = GameMenu.VisibleButtonRects()[0]; point = new Vector2(rect.center.x, Screen.height - rect.center.y);
            EatGui = true; MouseStateAt(point, true);
            yield return null; yield return null;
            Check(Get<bool>(_ui, "_open"), "Pause-menu MODS button opens despite consumed IMGUI events");
            MouseStateAt(point, false); EatGui = false;
            yield return null; yield return null;
            int pauseClicks = 0;
            pauseSettings.GetComponent<Button>().onClick.AddListener(() => pauseClicks++);
            AccessTools.Method(typeof(Button), "Press").Invoke(pauseSettings.GetComponent<Button>(), null);
            Check(pauseClicks == 0, "Pause-menu buttons cannot activate underneath MODS");
            Call(_ui, "Toggle");
            for (int i = 0; i < 4; i++) yield return null;
            Check(Time.timeScale == 0 && Cursor.lockState == CursorLockMode.None && Cursor.visible, "Closing to the pause-menu fixture preserves pause and a free cursor");
            Set(_ui, "_lastLock", CursorLockMode.Locked);
            Call(_ui, "OnApplicationFocus", false); Call(_ui, "OnApplicationFocus", true);
            yield return null; yield return null;
            Check(Cursor.lockState == CursorLockMode.None, "Focus regain does not lock the cursor inside the pause menu");
            AccessTools.Property(menuFsm.Fsm.GetType(), "ActiveState").SetValue(menuFsm.Fsm, menuFsm.Fsm.States[1], null);
            Time.timeScale = 1; Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
            _ui.Open(); yield return null;
            Check(Cursor.lockState == CursorLockMode.None && Cursor.visible, "Opening from gameplay frees the cursor immediately");
            Call(_ui, "Toggle");
            Check(Cursor.lockState == CursorLockMode.Locked, "Closing to gameplay restores the prior cursor lock immediately");
            for (int i = 0; i < 4; i++) yield return null;
            Check(!InputBlocker.Active, "Gameplay input resumes after closing from the fixture");
            // Simulate the recorded pre-focus state directly; this remains a title-scene fixture,
            // so its OS cursor behavior cannot substitute for a real gameplay Alt+Tab transition.
            Set(_ui, "_lastLock", CursorLockMode.Locked);
            Call(_ui, "OnApplicationFocus", false); Cursor.lockState = CursorLockMode.None;
            Call(_ui, "OnApplicationFocus", true); Call(_ui, "LateUpdate");
            Check(Cursor.lockState == CursorLockMode.Locked && !Get<bool>(_ui, "_restoreOnFocus"), "Gameplay focus recovery relocks once without repeated cursor resets");
            Destroy(gm); Destroy(pauseRoot); yield return null;
            titleCanvas.enabled = true; GameMenu.Invalidate(); Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            _ui.Open(); _ui.enabled = false;
            Check(!InputBlocker.Active && Cursor.lockState == CursorLockMode.None, "Disabling the UI cannot strand input blocking or a cursor lock");
            _ui.enabled = true;

            var sourceButtons = titleCanvas.GetComponentsInChildren<Button>(true).Where(b => b.enabled && b.name != "Apocasetter_Mods").ToArray();
            foreach (var sourceButton in sourceButtons) sourceButton.enabled = false;
            for (int i = 0; i < 3; i++) yield return null;
            Check(GameObject.Find("Apocasetter.MenuFallback/Apocasetter_Mods") != null && GameMenu.ButtonOnScreen, "Missing menu templates produce a visible native fallback");
            rect = GameMenu.VisibleButtonRects()[0]; point = new Vector2(rect.center.x, Screen.height - rect.center.y);
            EatGui = true; MouseStateAt(point, true); yield return null; yield return null;
            Check(Get<bool>(_ui, "_open"), "The native fallback opens MODS despite consumed IMGUI events");
            EatGui = false; MouseStateAt(point, false); yield return null; yield return null;
            Call(_ui, "Toggle");
            foreach (var sourceButton in sourceButtons) sourceButton.enabled = true;
            for (int i = 0; i < 4; i++) yield return null;
            Check(GameObject.Find("Canvas_MM/Apocasetter_Mods") != null && !GameObject.Find("Apocasetter.MenuFallback").transform.GetChild(0).gameObject.activeSelf,
                "Restoring native templates hides the fallback without duplicating the visible button");

            string settingsFile = Path.Combine(Path.GetDirectoryName(_output), "many-sections.cfg");
            var config = new BepInEx.Configuration.ConfigFile(settingsFile, false);
            var fixture = new ModEntry { Guid = "verification.categories", Name = "Many categories", Version = "1.0", Kind = ModKind.Loaded, OptedIn = true, Config = config };
            for (int i = 0; i < 45; i++) fixture.Entries.Add(config.Bind("Debug", "DebugEntry" + i, false));
            for (int i = 0; i < 20; i++) fixture.Entries.Add(config.Bind("Long category " + i, "Entry", false));
            _ui.Open();
            Catalog.All().Add(fixture);
            Set(_ui, "_selected", fixture.Guid); Set(_ui, "_settingSearch", "");
            Get<Dictionary<string, string>>(_ui, "_sectionSelections")[fixture.Guid] = "Debug";
            Drawn.Clear();
            yield return null; yield return null;
            Check(Drawn.Count == 45 && Drawn.All(e => e.StartsWith("Debug/")), "Categories with more than 40 settings render fully without SHOW ALL");
            Check(Get<int>(_ui, "_drawErrors") == 0, "A horizontally overflowing category strip renders without IMGUI exceptions");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(_output), "many-sections.png"));
            Call(_ui, "Toggle");
            for (int i = 0; i < 4; i++) yield return null;
            var rects = GameMenu.VisibleButtonRects();
            Check(ReferenceEquals(rects, GameMenu.VisibleButtonRects()), "Button geometry queries reuse their cached list");
            float scanAt = (float)AccessTools.Field(typeof(GameMenu), "_nextScan").GetValue(null);
            for (int i = 0; i < 30; i++) GameMenu.Tick(_ui.Open);
            Check((float)AccessTools.Field(typeof(GameMenu), "_nextScan").GetValue(null) == scanAt, "Repeated button updates do not repeat canvas discovery inside its throttle interval");
            TestRemoval();
            InputSystem.RemoveDevice(_mouse);
            _results.Add("COMPLETE: all checks passed");
        }

        private void TestRemoval()
        {
            string root = Path.Combine(Path.GetDirectoryName(_output), "fixtures-" + DateTime.UtcNow.Ticks);
            string bep = Path.Combine(root, "BepInEx"), data = Path.Combine(bep, "cache", "Apocasetter");
            Directory.CreateDirectory(data);
            var patcher = typeof(Apocasetter.Installer.Patcher);
            patcher.GetField("_root", Flags).SetValue(null, bep);
            patcher.GetField("_data", Flags).SetValue(null, data);
            patcher.GetField("_log", Flags).SetValue(null, Apocasetter.Plugin.Log);
            string oldData = Updates.DataDir;
            bool oldInstaller = Updates.InstallerPresent;
            Check(Updates.Ops.Count == 0, "No user operations are staged before isolated removal tests");
            Updates.DataDir = data; Updates.InstallerPresent = true;
            try
            {
                foreach (string scenario in new[] { "keep", "remove", "rollback" })
                {
                    string guid = "verification." + scenario;
                    string disabled = Path.Combine(data, "disabled", guid);
                    string files = Path.Combine(disabled, "plugins", "Fixture");
                    Directory.CreateDirectory(files);
                    File.WriteAllText(Path.Combine(files, "Fixture.dll"), "fixture DLL");
                    File.WriteAllText(Path.Combine(disabled, "manifest.json"), "{\"files\":[\"plugins/Fixture\"]}");
                    string config = Path.Combine(bep, "config", guid + ".cfg");
                    Directory.CreateDirectory(Path.GetDirectoryName(config)); File.WriteAllText(config, "fixture config");
                    var mod = new ModEntry { Guid = guid, Name = scenario, Version = "1.0", Kind = ModKind.Disabled,
                        DisabledInfo = new DisabledMod { Dir = disabled, Files = new List<string> { "plugins/Fixture" } } };
                    Call(_ui, "StageSimple", mod, "remove", null, scenario != "keep");
                    var op = Updates.OpFor(guid);
                    Check(((List<object>)op["paths"]).Single().ToString() == disabled, "Disabled removal stages the disabled copy directly (" + scenario + ")");
                    op["configPaths"] = new List<object> { scenario == "rollback" ? Path.Combine(root, "outside.cfg") : config };
                    bool failed = false;
                    try { patcher.GetMethod("MoveAway", Flags).Invoke(null, new object[] { op, false }); }
                    catch (TargetInvocationException) { failed = true; }
                    if (scenario == "rollback") Check(failed && File.Exists(Path.Combine(disabled, "manifest.json")) && File.Exists(Path.Combine(files, "Fixture.dll")), "Failed removal restores the disabled copy and manifest");
                    else
                    {
                        Check(!Directory.Exists(disabled), "Removed mod disappears from the disabled catalog (" + scenario + ")");
                        Check(Directory.GetFiles(Path.Combine(data, "removed"), "Fixture.dll", SearchOption.AllDirectories).Length > 0, "Removed disabled files are recoverable in backup (" + scenario + ")");
                        Check(File.Exists(config) == (scenario == "keep"), "Optional config removal is respected (" + scenario + ")");
                    }
                }
                // Discovering a disabled mod's primary config does not require a loaded ConfigFile.
                string probeGuid = "com.apocasetter.verification." + DateTime.UtcNow.Ticks;
                string cfg = Path.Combine(Paths.ConfigPath, probeGuid + ".cfg");
                string dataFolderName = probeGuid + ".data";
                string dataFolder = Path.Combine(Paths.ConfigPath, dataFolderName);
                string disabledProbe = Path.Combine(root, "config-discovery");
                try
                {
                    File.WriteAllText(cfg, "fixture config");
                    Check(new ModEntry { Guid = probeGuid, Name = probeGuid, Kind = ModKind.Disabled }.ConfigPaths().Contains(cfg), "Disabled mods still find their primary config for optional removal");
                    Directory.CreateDirectory(dataFolder);
                    Directory.CreateDirectory(Path.Combine(disabledProbe, "plugins", dataFolderName));
                    var dottedMod = new ModEntry { Guid = probeGuid, Name = "Other name", Kind = ModKind.Disabled,
                        DisabledInfo = new DisabledMod { Dir = disabledProbe, Files = new List<string> { "plugins/" + dataFolderName } } };
                    Check(dottedMod.ConfigPaths().Contains(dataFolder), "Disabled manifests preserve dotted folder names when discovering saved data");
                }
                finally { File.Delete(cfg); if (Directory.Exists(dataFolder)) Directory.Delete(dataFolder); }
            }
            finally { Updates.Ops.Clear(); Updates.DataDir = oldData; Updates.InstallerPresent = oldInstaller; Catalog.Invalidate(); }
        }
    }

    public class CountingModule : BaseInputModule
    {
        public static int Updates;
        public override void UpdateModule() { Updates++; }
        public override void Process() { Updates++; }
    }
}
