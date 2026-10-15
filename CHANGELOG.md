# Changes

## Unreleased — 2026-10-15

- Added BepInEx 5 preloader patcher discovery and a separate PATCHERS group, including the protected Apocasetter installer. Discovery uses Cecil
  metadata and excludes helper DLLs; it never initializes a patcher just to inspect its settings. GUID metadata/index mappings connect updates and
  trust categories. Shared-GUID runtime plugins and companion patchers are managed together, including their files and exposed config entries.
- Patcher DLL changes use atomic replacement and trigger a clean-launch completion step. The installer suppresses third-party patch callbacks
  during the transition launch, and the Mods window requires Quit/restart before playing. The restart marker is tied to the applying process so
  stale results cannot create a restart loop. Ordinary plugin operations keep their existing single-restart workflow. Installer version is 2.0.1.
- Numeric slider tracks and vertical/horizontal scrollbars now use cached steel cable textures with twisted strands and cut, frayed ends.
  A scoped GUI skin supplies matching scrollbar thumbs and restores the caller's skin afterward.
- Visual follow-up: restore the MODS button's rusty face by copying the source button's separate RawImage background, including its texture,
  material, UVs, and rectangle. Category tabs now use the main tabs' menu font and outlined uppercase labels, without brackets.
- Added independent Official / Community / Unverified toggles at the bottom of the mod list. They combine with All / Updates / Other and
  search, using the index's `trust` field. Official and Community are enabled by default; Unverified is hidden until enabled. Plugins without
  index metadata are Unverified. The top filter counts reflect the enabled trust categories.
- Widened the mod list by 40 virtual pixels. Long mod names, versions/status lines, badges, selected mod headings, restart summaries, and footer
  messages now use an ellipsis when they do not fit. Long badges are capped so they leave space for the mod name.
- Added Updates / Update frequency (`UpdateFrequency`): an integer slider from 0 (Each time / every game start) to 48 hours, defaulting to the
  previous six-hour cache interval. Its caption shows Each time or Every N h. Zero always bypasses the cache; manual checks remain immediate.
- Disabled mods now show **Remove** next to **Enable**. Removal stages the existing disabled directory directly, including its manifest,
  and moves it into the normal recoverable removal backup at the next start. The confirmation describes disabled files accurately and omits
  **Disable instead**. The optional settings removal finds the primary GUID config even without a loaded plugin, plus config data directories
  identified by the disabled manifest. The existing installer's directory move and rollback format is sufficient; no installer migration is needed.
- Added category tabs inside **Settings**, under the existing **Settings / About / What's new / Files** tabs. Tabs use the actual config section
  names, prefer General initially, remember each mod's selection for the session, and scroll horizontally when needed. Choosing a category clears
  the setting search and resets the settings scroll/focus. Search spans all categories. Removed Debug folding and the separate 40-entry collapse.
- Added input guards at `EventSystem.Update`, `Button.Press`, PlayMaker UI event dispatch, and PlayMaker's button callback. These protect title
  and pause menus even when an EventSystem appears or is re-enabled after opening. Existing systems are disabled once on open and restored after
  closing input drains; periodic EventSystem scans were removed. Unhandled mouse, key, and wheel events are consumed over the entire overlay.
- Closing keeps game input blocked through the final mouse/key release frame. The pointer itself is restored immediately. Menu closes always
  leave a visible, unlocked cursor; gameplay closes restore the prior cursor state. Removed repeated unlock/relock cycles after focus regain,
  cleared stale GUI, dropdown, binding, and slider capture on close, and added cleanup when the component is disabled or destroyed. The exact saved
  time scale is restored, including zero when the ESC menu was already paused. The update callback is unsubscribed on destruction.
- Reimplemented MODS injection in `MenuButton.cs`. New UGUI objects copy only image/font/button visual properties, avoiding game logic that
  could run during cloning. Input is polled independently of `OnGUI`, including before game UI dispatch; a native fallback replaces the IMGUI
  fallback. Injection recovers from removed buttons/components. Button canvases control rendering order without per-frame sibling reordering.
  Window drawing resets inherited GUI state so another mod leaving controls disabled or transparent does not affect its rendering.
  Native labels retain TextMeshPro support, while the IMGUI window keeps the source legacy font or its system fallback. Raw, uninstalled TMP
  font files are not handed to IMGUI; this prevents blank headings/buttons after reinjection with ApocaLanguage.
- Removed normal MODS button injection/template/visibility log chatter. Button construction diagnostics require VerboseLog; repeated identical
  injection errors are suppressed and retries are delayed. Cached menu, canvas, button, callback, and rectangle references reduce repeated searches
  and allocations. Scene changes invalidate menu references and schedule discovery immediately; background discovery is limited to every two seconds.
  Mouse polling also checks legacy input and non-current mouse devices, so a mod switching `Mouse.current` cannot discard a button press;
  closing input drains across those devices too.
- Added an opt-in Unity runtime verification plugin with temporary filesystem fixtures and screenshots. It is excluded from the shipped plugin.
  Both project files accept `-p:DeployToGame=false`, and `build.sh` includes the new source file and stops on compilation failure.

Validation and its limits are recorded in [verification/README.md](verification/README.md).
