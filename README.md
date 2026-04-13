# BuilderPlus

⚠️ **Work in Progress** — This mod is under active development. Expect bugs and missing features.

Replaces the vehicle editor UI in Kitten Space Agency with a KSP-style vertical building interface.

## Features

### UI
- Parts catalogue with category sidebar (Font Awesome icons) and search bar
- Vertical toolbar with icons (translate, rotate, scale, snap, connect, radial symmetry, stages, symmetry)
- Sequence panel with category icons per part
- Launch panel with body/location selection (locations filter by selected body)
- Stage panel (toggleable) with part highlighting and right-click to move parts between stages
- Save/Load vehicles with delete option
- Dark theme with rounded corners throughout
- Removed "Simulation Paused" alert in editor

### Parts & Building
- Vertical rocket orientation — parts spawn upright like KSP
- Click-to-grab parts (no need to hold click)
- Camera auto-centers on first part placement and when moving root part
- Delete parts by clicking the catalogue while holding a part
- Shift+Scale gizmo scales uniformly on all axes
- Undo/Redo system (max 50 states)

### Keyboard Shortcuts
| Key | Action |
|-----|--------|
| Delete | Remove selected part |
| R | Toggle radial symmetry |
| X / Shift+X | Cycle symmetry next/previous |
| C | Toggle angle snap |
| V | Toggle connect |
| W/S/A/D/Q/E | Rotate part (15°, 45° with Shift) |
| Ctrl+Z | Undo |
| Ctrl+Y | Redo |
| Scroll | Pan camera up/down |
| Shift+Scroll | Zoom in/out |

### Known Limitations
- Part rotation shortcuts (W/A/S/D/Q/E) don't work correctly when Snap mode is active

## Installation

1. Copy the `BuilderPlus` folder to `KSA/Content/` or `KSA/Mods/`
2. Make sure `fa-solid-900.font` is inside the `BuilderPlus` folder (do **not** rename it to `.ttf`)
3. Add to `manifest.toml`:
```toml
[[mods]]
id = "BuilderPlus"
enabled = true
```
4. Launch via StarMap

## Dependencies
- [StarMap Mod Loader](https://github.com/StarMapLoader/StarMap) >= 0.4.5

## Changelog

### v0.3.1
- Fixed bounding box not rotating with parts on launch, causing craft to spawn in the ground (credit: DevArchitect, tomservo)
- Fixed font loading: dynamic path resolution and renamed to `.font` extension to prevent Brutal's TTF glob from double-loading
- Removed broken `RegenerateFonts` hook, font now loads lazily on editor open

### v0.3.0
- Initial release with full custom editor UI
- Parts panel, toolbar, stage panel, sequence panel, launch panel
- Vertical rocket orientation with unrotate-on-launch
- Sticky grab, keyboard shortcuts, undo/redo, save/load

## Credits
- **DevArchitect** — Bounding box fix PR
- **tomservo** — Physics cache invalidation research and reference code

## License
MIT