# Apocasetter

In-game **Mods menu** for Apocalypter (BepInEx 5 plugin): settings for every mod, update notices, and install / update / remove
straight from the game.

Adds a native-looking **MODS** button to the title screen and the ESC pause menu. The window looks like the game's own main menu
(rust plate, rebar frame, wooden planks).

## Features

- **Mod list** with icons on the left: your mods, other BepInEx plugins, mods you disabled, and mods from the
  [Apocasetter index](https://github.com/DeonUrist/Apocasetter-Index) you don't have yet. Search and filters (All / Updates / Other).
  Official / Community / Unverified toggles sit below the list and combine with search and the top filters. Official and Community are on initially;
  Unverified is hidden until clicked. These use the index's `trust` category; plugins absent from the index are Unverified. The list is wider,
  and long names, badges, and status messages end with an ellipsis when they do not fit.
- **Settings** on the right, with category tabs below Settings / About / What's new / Files: General, Debug, and every other config section,
  using the same menu font as the main tabs, without surrounding brackets.
  General opens first when present; category choices are remembered per mod while the game runs. Long category strips scroll horizontally,
  and the setting search spans all categories. Every setting in a category is shown, including Debug and categories with more than 40 entries.
  Each entry has a readable name, the full description (long ones fold to two lines),
  default / range / "needs a restart" / changed tags, and a control picked from the entry type: ON/OFF planks, slider + field,
  ‹ value › lists, key bindings (click, then press a key; shared keys are flagged across all plugins and the game's F2 / F9),
  text fields. Reset per entry. Changes apply to the live `ConfigEntry` at once and the `.cfg` is saved.
- **Updates**: at game start Apocasetter checks the index and compares versions, reusing results for the configured Update frequency
  (0 = Each time; 1–48 hours; default 6 hours). The manual Check for updates button always checks immediately. Mods with a newer
  release get a badge in the list, the MODS button gets a counter, and the title screen shows a short notice.
- **Update / Install / Remove / Disable**: the zip is downloaded and checked against the index's SHA-256, then **staged**. The game holds
  every loaded DLL, so the change is applied by `Apocasetter.Installer` (a BepInEx patcher) at the next start, before any plugin loads:
  old files are backed up to `BepInEx\cache\Apocasetter\backup\`, the zip is unpacked on top (files the zip doesn't contain, like
  your own car templates, are kept). Removed and disabled mods are moved to `BepInEx\cache\Apocasetter\removed\` / `disabled\`, so
  nothing is deleted. A staged change can be undone until you restart.
  Disabled mods have **Remove** alongside **Enable**, so they can be removed directly. Their disabled copy and manifest are backed up together;
  removing their settings and saved data remains optional.
- **What's new** shows the release notes; **Files** shows what an update or removal touches.
- Game input and underlying UGUI menu input are blocked while the window is open (`Esc` closes it). The closing click/key and its release
  cannot activate the menu underneath. Closing preserves an existing pause, frees the cursor in menus, and restores its previous lock in gameplay.
- **Patchers**: standard BepInEx 5 preloader DLLs under `BepInEx/patchers` have a separate PATCHERS group in All and Other. Managed helper libraries
  are excluded. Exposed settings, config files, files, and indexed updates work with patcher entries; a runtime plugin and companion patcher with
  the same GUID share one entry. Patchers without index metadata remain Unverified, so enable that toggle to see them. Apocasetter's own installer
  is shown as an Official component and cannot be disabled or removed independently.
- Slider tracks and scrollbars use shaded steel cable with uneven cut ends and frayed wire tips. The textures are generated once and reused.

### Applying patcher changes

Patchers have already been loaded when the startup installer applies changes to their DLLs. After installing, updating, enabling, disabling,
or removing preloader DLLs, the transition launch opens the Mods window with **QUIT TO FINISH PATCHERS**. Quit and start the game once more to
load the complete new preloader set before playing. The normal Close button and hotkey cannot dismiss this completion step. This extra restart
applies to preloader file changes, including helper DLLs; ordinary plugin operations and patcher setting edits do not trigger it.

Patcher discovery reads assembly metadata without executing code. Settings are read only from ConfigFile fields of patcher types that the
installer observed after the preloader had run them. Unexposed config files can still be opened from Files.

## Installation

```
Apocalypter\
└── BepInEx\
    ├── patchers\
    │   └── Apocasetter.Installer.dll        applies staged updates / removals at start-up
    └── plugins\
        └── Apocasetter\
            ├── Apocasetter.dll
            └── theme\
                ├── theme.json + *.png         the older shared theme (Apocaspawner and others use it)
                └── game\                      the Mods window's look + mod icons
```

1. Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (win_x64) into the game folder and run the game once.
2. Unzip the release into the game folder (it has the `BepInEx\plugins` and `BepInEx\patchers` parts above).

Without `Apocasetter.Installer.dll` the window still works, but Update / Install / Remove are switched off.

Config: `BepInEx\config\com.denis.apocalypter.apocasetter.cfg`
- `[Keys] ToggleMenu` (default `F6`) opens the window from anywhere.
- `[General] RestoreCursorAfterAltTab` (default `true`) re-locks and hides the mouse cursor after Alt+Tab if it was locked before.
- `[Updates] CheckForUpdates` (default `true`) checks at game start using the configured cache interval. Nothing about you or your PC is sent.
- `[Updates] UpdateFrequency` (default `6`) hours to reuse results, from `0` (Each time / every game start) to `48` hours.
- `[Updates] UpdateNotice` (default `true`) badge on the MODS button and the title-screen notice.

## For mod makers: get your mod into the Mods window

**1. Show your settings.** Bind a `bool` entry named `Apocasetter` set to `true` (or put the line `Apocasetter = true` in your `.cfg`):

```csharp
Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
```

Every other entry of your config is then listed with its description, default and range. Write a clear description for each one:
players read it in full. Plugins without the line still show up under **Other plugins**, but only with their files.

**2. Add an icon.** A square PNG (64×64 is plenty; light lines on a transparent background look best, it is tinted yellow when selected):

- mod in its own folder → `BepInEx\plugins\YourMod\icon.png`
- single DLL directly in `plugins` → `YourMod.png` next to `YourMod.dll`

Put it in your release zip so it is installed with the mod. Without an icon the list shows your mod's initials.

**3. Get update notices and one-click install.**

1. Keep the mod in a **public GitHub repository**.
2. For every release: tag it with the version (`v1.2.0`), use the **same version in `[BepInPlugin]`**, and **attach a `.zip`** that unpacks into
   `BepInEx\plugins` (`YourMod.dll`, or a `YourMod\` folder with the DLL and its files; a README at the zip root is fine).
3. Submit the mod once with the **[Submit a mod form](https://github.com/DeonUrist/Apocasetter-Index/issues/new?template=submit-mod.yml)**.
   An automatic check comments within a minute or two; after review it is listed in the
   [Apocasetter index](https://github.com/DeonUrist/Apocasetter-Index).

From then on, each new release reaches players within the hour: they see an update badge and can install it from the Mods window.

**Mods your mod needs.** List their plugin GUIDs in the `requires` field of your index entry (the submit form has it) and declare them
in code too (`[BepInDependency("their.guid", BepInDependency.DependencyFlags.HardDependency)]`). Apocasetter then installs them along
with your mod (also when an update starts needing one), disables your mod when a player disables or removes one of them, and shows a
NEEDS … block with an INSTALL / ENABLE button if one is missing anyway. `optional` is for mods yours has extra features for.

## Using the shared GUI from another mod

`Theme`, `InputBlocker`, `GameMenu` and `Plugin` are public in namespace `Apocasetter`. Reference `Apocasetter.dll` and declare a
hard dependency:

```csharp
[BepInDependency(Apocasetter.Plugin.GUID, BepInDependency.DependencyFlags.HardDependency)]
```

- `Theme.Apply()` in `OnGUI`, plus `Theme.Header / Category / LeftPanel / RightPanel / SubLabel` styles.
- `InputBlocker.Set(bool)` — blocks PlayMaker input actions and sets `Time.timeScale = 0`.
- `GameMenu.InGame`, `GameMenu.Paused` (ESC menu open).
- `Plugin.Pressed(Key)` — new Input System key check with legacy fallback.

## Building

Requires the game's own assemblies. Either

- `dotnet build` (override the install path with `-p:GameDir="D:\path\to\Apocalypter"`); the DLL and theme are copied into
  `BepInEx\plugins\Apocasetter` automatically, or
- `./build.sh` with mono `mcs` (set `MANAGED` / `BEPCORE` to the game's `Apocalypter_Data\Managed` and `BepInEx\core` folders);
  it builds `Apocasetter.dll` and `Apocasetter.Installer.dll` (or `dotnet build Installer\Apocasetter.Installer.csproj`).

Use `-p:DeployToGame=false` with either project to build without copying files into the game.
The shell build stops immediately if either compilation fails. Runtime regression checks and their opt-in runner are documented in
[verification/README.md](verification/README.md). User-visible changes are recorded in [CHANGELOG.md](CHANGELOG.md).

Compile against the game's own `mscorlib`/`System` — newer BCL overloads throw `MissingMethodException` at runtime.

## Notes

- The game destroys plugin GameObjects on scene load, so per-frame logic runs on a hidden `DontDestroyOnLoad` runner.
- Title screen and pause menu are different canvases. MODS buttons are built from visual properties of a visible game menu button;
  no live game object, FSM, confirmation dialog, or persistent click listener is cloned. A native UGUI fallback covers missing menu templates.
  The game button's separate RawImage background is copied as a visual child, preserving its rusty button image.
- MODS click detection runs through the Input System (legacy fallback) from Update and before EventSystem dispatch. Other mods consuming
  IMGUI events or leaving `GUI.enabled` false cannot prevent the native button from opening the window. The settings pane itself still uses IMGUI.
- Canvas/button discovery runs at most once every two seconds and immediately after a scene change. Cached references serve per-frame visibility
  checks; button rectangle queries reuse their list. Deleted buttons or missing button components are rebuilt. Separate button canvases keep them
  above menu siblings without repeatedly reordering the hierarchy. Buttons hide while an overlay blocks input.
- Routine button injection, template dumps, and show/hide messages are absent from normal logs. `[Debug] VerboseLog` includes a short message
  when a button is built. Injection failures are logged once per distinct message per canvas and retries wait five seconds.
- Native button labels support legacy and TextMeshPro fonts. The IMGUI window uses the source legacy font when available and keeps its system
  fallback for TMP-only templates: raw TextMeshPro font files can otherwise leave IMGUI labels blank after reinjection with ApocaLanguage.
- `SettingsUI.OnGUI` / `SettingsUI.Draw` keep their names: ApocaLanguage hooks them to translate the window.
