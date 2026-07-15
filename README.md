# Paralives Controller Mod

Full Xbox-controller support for [Paralives](https://store.steampowered.com/app/1118520/Paralives/), built on BepInEx. Play the whole game from the couch: menu navigation with real focus highlights, a BG3-style virtual cursor for everything pointy, sensible default bindings for every PC shortcut, Xbox button glyphs in tooltips and hint bars, and full rebinding via config file.

## Disclaimer

This is my first project of this type. I take no responsibility for corruption of game saves or issues experienced with the mod. Please download at your own risk. I only work on this when I have time, this is by no means official or remotely finished. This was a personal project that I wanted to do and I just wanted to share in case anyone found it useful.

## Features

- **Menu navigation everywhere** — main menu, pause menu, interaction menus, the build catalog (including the item grid, with auto-scroll), and the settings screen (sliders adjust with left/right) are all D-pad navigable with a visible focus highlight. A confirms, B backs out.
- **Virtual cursor** — click the left stick to summon a gold reticle you drive with the right stick; it clicks anything the mouse can (world objects and all UI), then toggles away. The OS mouse pointer stays hidden while you're on controller.
- **Time control on the D-pad** — left steps the game speed down (eventually pausing), right steps it up. Up/down switch floors, as the game intended.
- **Every PC shortcut has a controller equivalent** — undo/redo, quick save, build tools, photo mode, town map, family tree, calendar, item rotation and duplication, and more (full table below).
- **Xbox button glyphs** — tooltips and the keybinding hint bar show real controller icons (art from Kenney's CC0 Input Prompts) instead of keyboard keys while a controller is active.
- **Fully rebindable** — every binding the mod adds lives in a BepInEx config file you can edit by hand or with ConfigurationManager.

## Requirements

- Paralives (Steam, Windows)
- [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx/releases) — the **x64** build (`BepInEx_win_x64_5.4.*.zip`)

## Installation

1. Extract the BepInEx zip into your Paralives folder (the one containing `Paralives.exe`, usually `C:\Program Files (x86)\Steam\steamapps\common\Paralives`).
2. Launch the game once and quit — BepInEx creates its folder structure on first run.
3. Extract this mod's release zip into the same Paralives folder. It places `ControllerMod.dll` in `BepInEx\plugins\`.
4. Launch the game with a controller connected. Press any button on the pad to switch the game to controller mode.

To uninstall, delete `BepInEx\plugins\ControllerMod.dll`.

## Controls

| Input | Action |
| --- | --- |
| **A** | Confirm / click (focused menu item, or whatever the virtual cursor points at) |
| **B** | Cancel / back |
| **X** | Delete / sell selected object |
| **Y** | Open / close build catalog |
| **Menu** (Start) | Pause menu |
| **View** (Select) | Town map |
| **LB / RB** | Switch tabs while a menu is open |
| **LB + X** | Rotate selected item 90° |
| **RB + X** | Duplicate selected item |
| **LB + View** | Toggle Live / Build mode |
| **LB + Y** | Lot mode |
| **RB + View** | Family tree |
| **RB + Menu** | Calendar |
| **Left stick** | Move camera |
| **Right stick** | Camera look / move the virtual cursor when it's active |
| **Left stick click** | Toggle the virtual cursor |
| **Right stick click** | Photo mode |
| **D-pad up / down** | Floor up / down (navigates menus while one is open) |
| **D-pad left / right** | Game speed slower / faster — left eventually pauses, right resumes |
| **D-pad left / right (item held or selected)** | Rotate the item 45° counterclockwise / clockwise |
| **LT / RT** | Camera zoom (the game's native binding) |
| **LT (hold while placing)** | No Snap — free placement while an item or wall is being placed/moved/resized |
| **RT (hold while placing)** | Move Vertically (items) / grid divide (walls) |
| **LB + D-pad left / right** | Undo / Redo |
| **LB + D-pad up / down** | Quick save / Center camera on selected character |
| **RB + D-pad left / right** | Wall tool / Pipette tool |
| **RB + D-pad down / up** | Sledgehammer / Toggle build grid |

While any menu is open, the D-pad and left stick move focus, A activates, and B backs out — combo bindings and floor switching are automatically suspended so they can't fire behind the menu. The exception is while you're **carrying an item**: even with the catalog on screen, the D-pad rotates the item (left/right) and switches floors (up/down), A places it, and B cancels — menu navigation resumes the moment the item is placed or dropped.

The triggers do double duty: they zoom the camera normally, but while you're actively holding an item (or drawing a wall), LT becomes **No Snap** and RT becomes **Move Vertically** — zoom is suspended for just that moment so the two roles never fight. Rebind or disable them with `NoSnapHold` / `MoveVerticallyHold` in the config.

### The virtual cursor

Click the left stick and a reticle appears. Move it with the right stick, click with A. It behaves exactly like the mouse — world objects, build catalog items, sliders, anything. Click the left stick again to dismiss it and return the sticks to camera control. With the cursor hidden, pressing A inside menus will never click through to the world behind them.

Cursor speed is configurable: `SpeedPixelsPerSecond` under `[Virtual Cursor]` in the config file (default 1000 — how many screen pixels it covers per second at full stick deflection). The toggle button is configurable there too (`ToggleButton`).

## Rebinding

All mod bindings live in `BepInEx\config\com.zorrph.paralives.controllermod.cfg` (created on first launch). Edit with any text editor while the game is closed, or in-game with [ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

- Values are [Unity Input System control paths](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.4/manual/Controls.html#control-paths), e.g. `<Gamepad>/buttonNorth`.
- Join two paths with `+` for a hold-modifier combo, e.g. `<Gamepad>/leftShoulder+<Gamepad>/buttonWest`.
- An empty value removes the gamepad binding for that action.
- `[Advanced] DebugLogging = true` turns on verbose diagnostics — please enable it and attach `BepInEx\LogOutput.log` when reporting bugs.

## Reporting issues

Found a bug or a menu the controller can't reach? [Open an issue](https://github.com/zorrph/paralives-controller-mod/issues) — reports are genuinely welcome. The most useful reports include:

1. **What you did and what happened** — e.g. "opened the build catalog, pressed D-pad down, focus jumped to the wrong item."
2. **Your log file.** Set `DebugLogging = true` under `[Advanced]` in `BepInEx\config\com.zorrph.paralives.controllermod.cfg`, reproduce the problem, then attach `BepInEx\LogOutput.log` from your Paralives folder. The debug log records exactly which buttons fired which actions.
3. **Your setup** — controller model, mod version, and whether the game updated recently.

## Known limitations

- Text-input fields (e.g. name boxes) still need the virtual cursor and a keyboard.
- Chair-slot snapping ignores the LT No Snap modifier (the game checks the keyboard directly for that one case).
- Glyphs are Xbox-style regardless of your controller brand.

## Building from source

```powershell
dotnet build ControllerMod.csproj -c Release -p:GamePath="C:\path\to\Paralives"
```

`GamePath` must point at a Paralives install that has BepInEx installed (the build references the game's assemblies from there — they are not distributed with this repo). The glyph atlas is embedded into the DLL at build time; `release.ps1` builds, stages the zip layout, and can publish a GitHub release.

## Credits

- Controller glyph art: [Kenney Input Prompts](https://kenney.nl/assets/input-prompts) (CC0)
- Built with [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony)

## License

See [LICENSE](LICENSE).
