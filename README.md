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

Put it in your release zip so it is installed with the mod. The index also copies it out of the zip (PNG up to 256×256 and
48 KB), so players see it in the list before they install the mod. Without an icon the list shows your mod's initials.

**3. Get update notices and one-click install.**

1. Keep the mod in a **public GitHub repository**.
2. For every release: tag it with the version (`v1.2.0`), use the **same version in `[BepInPlugin]`**, and **attach a `.zip`** that unpacks into
   `BepInEx\plugins` (`YourMod.dll`, or a `YourMod\` folder with the DLL and its files; a README at the zip root is fine).
3. Submit the mod once with the **[Submit a mod form](https://github.com/DeonUrist/Apocasetter-Index/issues/new?template=submit-mod.yml)**.
   An automatic check comments within a minute or two; after review it is listed in the
   [Apocasetter index](https://github.com/DeonUrist/Apocasetter-Index).

From then on, each new release reaches players within the hour: they see an update badge and can install it from the Mods window.

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
