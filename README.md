# BetterBuilder (WIP)

⚠️ **Work in Progress** — This mod is under active development. Expect bugs and missing features.

Replaces the vehicle editor UI in Kitten Space Agency with a KSP2-style interface.

## Features
- Parts catalogue with category sidebar and search
- Vertical toolbar (translate, rotate, scale, snap, connect, symmetry)
- Stage panel with hover highlight
- Sequence panel
- Launch panel with body/location selection
- Click-to-grab parts (no need to hold click)
- Delete parts by clicking the catalogue while holding a part
- Save and load vehicles (option to delete saved vehicles)
- Undo and redo key bindings and buttons
- Removed "Simulation Paused" alert on VAB
- Visual - Rounded the corners of elements to make it more visually pleasing

## Planned
- Right-click to move parts between stages
- Sequence panel icons
- Visual polish
- Middle Mouse to Pan around editor
- Modifier (like shift) for the Scale Gizmo to scale in multiple dimensions
- Delete key to remove parts
- SVG better Icons for categories (Currently Font Awesome is being used, please install it in your system as I think is needed)
- Drag and drop to arrenge stages

## Dependencies
- [StarMap Mod Loader](https://github.com/StarMapLoader/StarMap) >= 0.4.5

## Installation
1. Copy the `BetterBuilder` folder to `KSA/Content/`
2. Add to `manifest.toml`:
```toml
[[mods]]
id = "BetterBuilder"
enabled = true
```
3. Launch via StarMap

## License
MIT
