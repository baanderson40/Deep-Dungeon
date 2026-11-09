# Boss Mod development version

Boss Mod (VBM) is a Dalamud plugin for FFXIV. It provides encounter radar, autorotation, cooldown planning, and AI-assisted movement. This development version may include unfinished behavior and diagnostics; use with care.

## Features

- **Encounter radar:** displays player and enemy positions, mechanics, and imminent area attacks.
- **Autorotation:** automates supported job rotations, including action timing, target selection, and movement-aware casting.
- **Cooldown planner:** organizes job and role actions around encounter mechanics.
- **AI movement:** moves toward safe positions and navigation goals supplied by encounter modules.
- **Deep Dungeon support:** offers floor navigation, hazard avoidance, and configurable pomander automation.

## Settings

Open VBM settings through the Dalamud Plugin Installer or with `/vbm`. Features can be configured individually. Deep Dungeon automation settings are available in the Deep Dungeon module configuration.

For Deep Dungeon navigation, VBM owns automatic movement and objectives. Disable other automation that controls movement or pomanders while using those features. Keep hazard hints enabled when relying on automatic navigation.

## License

Boss Mod is licensed under the [BSD 3-Clause License](LICENSE).
