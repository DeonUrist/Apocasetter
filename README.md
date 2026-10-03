# Apocasetter

In-game **Mods menu** for Apocalypter (BepInEx 5 plugin): settings for every mod, update notices, and install / update / remove
straight from the game.

Adds a native-looking **MODS** button to the title screen and the ESC pause menu. The window looks like the game's own main menu
(rust plate, rebar frame, wooden planks).

## Features

- **Mod list** with icons on the left: your mods, other BepInEx plugins, mods you disabled, and mods from the
  [Apocasetter index](https://github.com/DeonUrist/Apocasetter-Index) you don't have yet. Search and filters (All / Updates / Other).
- **Settings** on the right, grouped by config section: readable name, the full description (long ones fold to two lines),
  default / range / "needs a restart" / changed tags, and a control picked from the entry type: ON/OFF planks, slider + field,
  ‹ value › lists, key bindings (click, then press a key; shared keys are flagged across all plugins and the game's F2 / F9),
  text fields. Reset per entry. Changes apply to the live `ConfigEntry` at once and the `.cfg` is saved.
- **Updates**: once per game start (results kept 6 hours) Apocasetter downloads the index and compares versions. Mods with a newer
  release get a badge in the list, the MODS button gets a counter, and the title screen shows a short notice.
- **Update / Install / Remove / Disable**: the zip is downloaded and checked against the index's SHA-256, then **staged**. The game holds
  every loaded DLL, so the change is applied by `Apocasetter.Installer` (a BepInEx patcher) at the next start, before any plugin loads:
  old files are backed up to `BepInEx\cache\Apocasetter\backup\`, the zip is unpacked on top (files the zip doesn't contain, like
  your own car templates, are kept). Removed and disabled mods are moved to `BepInEx\cache\Apocasetter\removed\` / `disabled\`, so
  nothing is deleted. A staged change can be undone until you restart.
- **What's new** shows the release notes; **Files** shows what an update or removal touches.
- Game input is blocked and time is paused while the window is open (`Esc` closes it).

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
- `[Updates] CheckForUpdates` (default `true`) downloads the index once per game start. Nothing about you or your PC is sent.
- `[Updates] UpdateNotice` (default `true`) badge on the MODS button and the title-screen notice.

## Making your mod appear in the menu

Bind a `bool` config entry named `Apocasetter` and set it to `true`:

```csharp
Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
```

(A bare `Apocasetter = true` line in the plugin's `.cfg` also works.)

Plugins that don't opt in are still listed under **Other plugins** (files, remove / disable, and their settings on request).

**Icon**: put a square `icon.png` (64×64, white on transparent works best) in your plugin's folder, or `<YourDll>.png` beside a DLL that
sits directly in `plugins`.

**Updates**: get your mod into the [Apocasetter index](https://github.com/DeonUrist/Apocasetter-Index) (issue form or pull request).
Your releases need a `.zip` with the plugin DLL attached.

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

Compile against the game's own `mscorlib`/`System` — newer BCL overloads throw `MissingMethodException` at runtime.

## Notes

- The game destroys plugin GameObjects on scene load, so per-frame logic runs on a hidden `DontDestroyOnLoad` runner.
- Title screen and pause menu are different canvases; the MODS button is cloned from the game's own `Settings` button on each.
- The window picks up the game's menu font from that cloned button when it is a legacy `Font` (else Impact).
- `SettingsUI.OnGUI` / `SettingsUI.Draw` keep their names: ApocaLanguage hooks them to translate the window.
