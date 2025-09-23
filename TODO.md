# TODO

Work left and work finished on OpenTribes, compared with the original Tribes 1.11.

This file is updated in the same commit as each fix or feature: the item moves to Done, and new gaps found along the
way are added to Open.

## Open

### Client

- [ ] Joystick support.
- [ ] Hosting a game from the client menus.
- [ ] The intro movie.

### Project

- [ ] Four tests (`PlayerTests.Main`, `VehicleTests.Main` and two `WorldTests` vector tests) fail because their data files are in the C++ repository (`OpenTribes-cpp/tests`).
- [ ] Try the builds on Windows ARM64, Linux ARM64 and both macOS targets.

## Done

### Server

- [x] Networking core and the server program, with the telnet console and server commands. Oversized packets are cut like the original.
- [x] Console script language: lexer, parser, interpreter, built-in functions, math commands, number formatting and the random number generator.
- [x] Client command table, client commands and key table.
- [x] Ban list and console log.
- [x] `scriptrun` runs console scripts outside the game.
- [x] Numbers round toward zero like the original: stored floats, parsing and arithmetic. Overflow gives infinity.
- [x] Simulation layer: sim manager, number helpers, text scanning, string table and world code (material lists and shapes), used by `scriptrun` and the server.
- [x] Datablocks, their tables and their network packing.
- [x] Game commands: backlog, client game commands, command map, game, static shape, terrain and utility commands.
- [x] Game objects: control object, damage, displacement, game state, items, mission objects, sensors, shape state and static shapes.
- [x] Game connection, ghost manager, ghost classes and ghost scope. The connection's objects are visible to scripts.
- [x] Object files.
- [x] Player manager, with the sim objects it needs.
- [x] Turret simulation: search, fire and control.
- [x] The server hosts a mission from the game directory.
- [x] Players, mounts, item images, projectiles, fire paths, vehicles, fliers and turrets.
- [x] Bots: AI object, skill, steering, the AI tick and AI commands. The skill constant and the laser aim round like the original.
- [x] Master server heartbeat and master commands.
- [x] Tag id 0 is ignored and negative tag ids cannot be looked up.
- [x] Input consumers and GUI classes on the server side.
- [x] File search restarts after reaching the end.
- [x] Files of an unmounted volume stay available until purged, also for mission and world loading.

### Client network

- [x] Reads the game stream, ghosts and `.rec` demos (`tribes-netclient`).
- [x] Prediction of the player's own movement, corrected by the server, also in `tribes-netclient`.
- [x] The client process has a GUI and a network session.
- [x] `tribes` runs the stock scripts to the main menu.
- [x] Joining a server, the server list and the play view.

### Graphics

- [x] Terrain, interiors and sky of a mission.
- [x] 3D camera and shape support in the renderer.

### Shapes, weapons and effects

- [x] Shape loading and animation.
- [x] Players, shapes and effects drawn from a demo.
- [x] Lightning sway and rain streak size.
- [x] First-person weapon, vertical field of view and item commands.

### Menus and HUD

- [x] GUI: `.gui` loader and writer, canvas, class table, console commands, controls (including lists and widgets) and the HUD.
- [x] Screenshots of GUI screens.
- [x] Control names, text list values and list selection.
- [x] Chat entry text and stable number settings.
- [x] Quit, message window frame, chat entry layout and energy bar.
- [x] Jet pack frame nests under the health frame.
- [x] Loading progress for a command line join.
- [x] Inventory and score lists, working in the client.
- [x] Version text on the main menu and team colours by team id.
- [x] Command screen frame, menu panel, box frame and score window fonts.
- [x] Weapon, compass, clock and sensor HUDs and the centre print box.
- [x] Progress bar, formatted text fonts and alignment.

### Audio

- [x] Sound output and mixer.
- [x] Sound manager, sound commands and sounds in the world.
- [x] Player animation, sequence and turret power sounds.

### Input and controls

- [x] Keyboard and mouse input routing.
- [x] Timed input scripts, chat and play view cursor.
- [x] Relative mouse mode changes only when needed.

### Platforms

- [x] Builds for Windows, Linux and macOS on x64 and ARM64, and a per-user data folder for a read-only install.
- [x] Frame timing log, fewer allocations per frame and no stall on the first terrain draw.
