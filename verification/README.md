# Runtime verification

`RuntimeProbe.csproj` builds a temporary BepInEx plugin that exercises Apocasetter inside Unity 2020.3.49f1 Mono, against the installed game's
assemblies. It is excluded from `Apocasetter.csproj` and is not part of the normal build or release.

The probe runs only with `--apocasetter-verify`. It stays at the title screen, creates temporary pause/play FSM and canvas fixtures to check
the game's documented pause-menu contracts, and exits the game when finished. It never loads a save. Installer checks operate under a separate
fixture BepInEx root. A uniquely named config discovery fixture is created in the game config directory and removed in a `finally` block.

## Running

Close the game first. Do not run with staged operations in `BepInEx/cache/Apocasetter/pending.json`: starting the game would apply them.

```powershell
dotnet build -c Release
dotnet build Installer/Apocasetter.Installer.csproj -c Release
dotnet build verification/RuntimeProbe.csproj -c Release

$gameRoot = 'E:\SteamLibrary\steamapps\common\Apocalypter'
New-Item -ItemType Directory -Force verification/artifacts | Out-Null
Copy-Item verification/bin/Release/Apocasetter.Verification.dll "$gameRoot/BepInEx/plugins/Apocasetter.Verification.dll"
# Use an absolute output path; keep the game focused for raw pointer checks.
& "$gameRoot/Apocalypter.exe" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 `
  --apocasetter-verify --apocasetter-verify-output=V:\Repos\Apocalypter\Apocasetter\verification\artifacts\runtime-results.txt
# After the game exits:
Remove-Item -LiteralPath "$gameRoot/BepInEx/plugins/Apocasetter.Verification.dll"
```

The report ends with `COMPLETE: all checks passed`, or records the failing assertion and stack. Screenshots and isolated removal fixtures are
left beside the report for inspection. Generated artifacts are ignored by Git. Keep the probe out of release packages and remove it from the
game after testing, including failed runs. Use the game's BepInEx log to check for unexpected exceptions and routine MODS button log messages.

## Coverage

- Native title-menu injection without copied FSMs or persistent callbacks; rebuilding after another mod deletes the button.
- Translated native labels preserve a usable legacy font for the IMGUI window after reinjection; screenshots also check for blank headings.
- Raw mouse opening while a Harmony prefix consumes IMGUI mouse/key events and leaves GUI disabled and transparent, with another device forced
  to be `Mouse.current` after the synthetic mouse press.
- Real category rendering, General as default, explicit Debug settings without folding, and search across categories.
- More than 40 settings in a category and a horizontally overflowing strip with 21 categories.
- Underlying button dispatch and a newly created, enabled EventSystem are blocked while the overlay is open.
- Held closing input stays blocked; its release frame cannot activate the underlying menu; normal input resumes afterward.
- Exact paused/unpaused time-scale restoration, immediate free cursor in menus, gameplay lock restoration, one-time focus recovery,
  and cleanup when the UI component is disabled.
- An in-runtime pause-menu fixture verifies detection of `__GameManager__ [Menu]` states and injection into `MainMenu_Canvas`, using the same
  raw-click and underlying-button guards as the title menu.
- Reused rectangle lists and throttled discovery under repeated button updates.
- Disabled removal stages its cache directory directly; files and manifest are backed up together; config keep/remove choices are respected;
  an invalid path rolls the directory move back. Primary GUID config discovery works without a loaded ConfigFile.

## Limits

The pause/play checks use fixtures based on `Sources/systems/world_ui.md` and the menu FSMs, not a loaded gameplay save. Focus recovery is exercised
through the component's focus/LateUpdate transition with a recorded cursor state, rather than OS-level Alt+Tab automation. The test deliberately
models interfering IMGUI behavior; it cannot establish compatibility with every third-party mod. The settings content still uses IMGUI,
so unrelated mods that deliberately suppress its draw method or replace the input stack require separate compatibility investigation.

## Recorded result — 2026-10-15

- Started from clean HEAD `2ef26be`; the baseline build succeeded before edits.
- Final plugin, installer, and verification plugin builds succeeded with zero warnings and errors. The installer was also built with deployment
  disabled. The local game plugin DLL matches the final build by SHA-256.
- **54 runtime assertions passed**, including the translated-font regression. The final report is `artifacts/runtime-results.txt`.
- The installed game used Unity 2020.3.49f1 Mono, with ApocaLanguage 1.5.6 in Russian and Apocaspawner 2.0.8 present.
- Visually inspected `artifacts/general.png`, `artifacts/debug.png`, and `artifacts/many-sections.png` at 1280×720. The latter shows 21 category
  tabs in a horizontal strip and 45 Debug settings; headings and buttons remain visible after translated native-button reinjection.
- The captured BepInEx log (`artifacts/BepInEx-runtime.log`) contains no unexpected error/exception entries and no routine MODS button
  injection/template/visibility messages with VerboseLog disabled.
- The verification game exited, the temporary probe DLL was removed, and no operations were staged in the real game cache. No save was loaded.

## Follow-up changes — 2026-10-15

The button-background, category-tab styling, wider list/ellipsis, trust toggles, and update-frequency changes were built afterward with zero
warnings and errors. The game was already running, so the full Unity probe and screenshot run above was not repeated for these follow-ups.
The updated DLL was replaced on disk and its SHA-256 matches the build; a game restart is required to load it.

**13 headless checks passed** against the compiled code (`artifacts/followup-checks.txt`): the default six-hour interval, zero always bypassing
the cache, fresh/expired results at one and 48 hours, clamping to 0–48, absence of a cache, and case-insensitive Official / Community /
Unverified classification with unindexed plugins treated as Unverified. Trust filters default to Official and Community enabled and Unverified
disabled. The restored button background follows the RawImage child documented in both vanilla menu hierarchies in `Sources/data/hierarchy/`.

## Patcher and cable follow-up — 2026-10-15

The headless check sources are versioned under `headless/`; their outputs and fixtures stay under ignored `artifacts/`.
After building the plugin and installer in Release with `-p:DeployToGame=false`, run:

```powershell
dotnet build verification/headless/CacheChecks.csproj -c Release
& ./verification/artifacts/headless/bin/Release/net472/CacheChecks.exe
```

**38 headless checks passed** (`artifacts/patcher-checks.txt`), including the earlier 13 cache/trust checks. A temporary BepInEx root and a generated
patcher DLL exercise metadata/GUID/version discovery, exclusion of helper libraries, no initializer execution during discovery, initialized live
ConfigFile entries, shared-GUID companion packages, disable/enable/update/remove, atomic replacement of an already loaded patcher DLL, suppression
of transition callbacks, process-scoped restart results, and ordinary plugin operations avoiding the additional restart. All fixtures remain under
the ignored artifacts directory; no real patchers or user configs are altered by these tests.

The pure cable pixel generator is tested for valid alpha/color output and transparent cut ends. Its preview is
`artifacts/headless/bin/Release/net472/cable-preview.png`; it was visually inspected. Actual slider/scrollbar interactions are unchanged, and the
GUI skin is scoped to the Mods window. The game was running during these changes, so the new skin has not had a fresh in-game screenshot pass.

The lifecycle design follows [BepInEx 5's AssemblyPatcher source](https://github.com/BepInEx/BepInEx/blob/v5-lts/BepInEx.Preloader/Patching/AssemblyPatcher.cs):
DLL discovery/load precedes patcher initialization and assembly patching. Patchers changed by the installer therefore finish on a separate clean
launch. Initializers that ran before Apocasetter's installer cannot be undone in the transition process, which is why gameplay stays blocked there.

Both final Release builds completed with zero warnings/errors, and `git diff --check` passed. Deployment of this patcher/cable follow-up was
rejected by automatic approval review with only "blocked by policy" returned; neither new DLL was copied by that attempt. Build outputs remain
at `bin/Release/Apocasetter.dll` and `Installer/bin/Release/Apocasetter.Installer.dll` for installation together.
