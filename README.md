# Apocasetter

In-game **Mods settings menu** for Apocalypter (BepInEx 5 plugin).

Adds a native-looking **MODS** button to the title screen and the ESC pause menu. It opens a window that lists every BepInEx plugin
that opts in and lets you edit its config live, without leaving the game.

## Features

- Plugin list on the left, settings on the right grouped by config section, with search.
- Controls picked from the config entry type: bool → toggle, enum / `AcceptableValueList` → dropdown, numeric with
  `AcceptableValueRange` → slider + field, everything else → text field. Per-entry **Reset** to default, description under each row.
- Changes are applied to the live `ConfigEntry` immediately (toggles, dropdowns, Reset) or on **Save** / Enter / close (text fields),
  so mods reading `.Value` see them at once; the `.cfg` is saved as well. **Reload from file** re-reads the `.cfg`.
- Game input is blocked and time is paused while the window is open (`Esc` closes it).
- Ships a shared GUI theme (`theme/theme.json` + 9-slice PNGs) that other mods can reuse.

## Installation

The mod is two parts: the plugin DLL **and** the GUI theme folder (`theme.json` + the PNG 9-slices that skin the window,
buttons, categories, scrollbars and text fields). Both must be installed together, in this layout:

```
Apocalypter\
└── BepInEx\
    └── plugins\
        └── Apocasetter\
            ├── Apocasetter.dll
            └── theme\
                ├── theme.json
                ├── window.png
                ├── panel.png
                ├── button.png / button_hover.png / button_active.png
                ├── category.png / category_hover.png / category_selected.png
                ├── scrollbar.png / scrollbar_thumb.png
                └── textfield.png
```

1. Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (win_x64) into the game folder and run the game once.
2. Create `BepInEx\plugins\Apocasetter\` and copy `Apocasetter.dll` **and the whole `theme` folder** into it.

The theme is looked up relative to the DLL (`<plugin dir>\theme\theme.json`). Without it the mod still works but falls back to the
plain Unity IMGUI skin, and the log shows `No theme.json at ...`. Other mods that use the shared GUI (e.g. Apocaspawner) get their
look from this same theme folder, so they need it too.

Config: `BepInEx\config\com.denis.apocalypter.apocasetter.cfg` — `[Keys] ToggleMenu` (default `F6`) opens the window from anywhere;
`[General] RestoreCursorAfterAltTab` (default `true`) re-locks and hides the mouse cursor when the game regains focus after Alt+Tab if it was locked before (Unity releases the lock on focus loss and the game never re-applies it, so the cursor would otherwise stay on screen).

## Making your mod appear in the menu

Bind a `bool` config entry named `Apocasetter` and set it to `true`:

```csharp
Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
```

(A bare `Apocasetter = true` line in the plugin's `.cfg` also works.)

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
- `./build.sh` with mono `mcs` (set `MANAGED` / `BEPCORE` to the game's `Apocalypter_Data\Managed` and `BepInEx\core` folders).

Compile against the game's own `mscorlib`/`System` — newer BCL overloads throw `MissingMethodException` at runtime.

## Notes

- The game destroys plugin GameObjects on scene load, so per-frame logic runs on a hidden `DontDestroyOnLoad` runner.
- Title screen and pause menu are different canvases; the MODS button is cloned from the game's own `Settings` button on each.
