# TODO

Work left and work finished on OpenTribes, compared with the original Tribes 1.11.

This file is updated in the same commit as each fix or feature: the item moves to Done, and new gaps found along the
way are added to Open.

## Open

### Vehicles

- [ ] Riders on turrets.

### Client

- [ ] Joystick support.
- [ ] Hosting a game from the client menus.
- [ ] Our voice chat plays about 3 dB quieter than the original.
- [ ] No sound when switching to the blaster or the mortar.
- [ ] Server browser: refresh, favourites and "never ping".
- [ ] Far hills and the in-game sky brightness.

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
- [x] Weapon state steps once per predicted move.
- [x] Packet pacing, rate requests and their limits.
- [x] Terrain and network settings with their default values, including prediction timing.
- [x] Smooth movement of other players and vehicles, riders included, in the right order. Dead players fall to the ground.
- [x] Model checks when joining a server, with the file check messages.
- [x] Vehicle camera.

### Graphics

- [x] Terrain, interiors and sky of a mission.
- [x] 3D camera and shape support in the renderer.
- [x] Mission palette stays active in the play view.
- [x] Terrain textures, light map, lighting, orientation and level of detail, and team skins on interiors.
- [x] Smooth light maps placed and stored like the original.
- [x] Smooth texture magnification.
- [x] Shape face culling.
- [x] Interior and sky texture colours and sky band size.
- [x] Dynamic lights on interiors and terrain, in the original steps.
- [x] Team skins change with the team.
- [x] Rain and snow follow the weather setting, with the original streak count, size, speed, colour and direction.
- [x] Sky panels.
- [x] Interior lights that start on their own, timed in game time, per interior.
- [x] Depth buffer like the original.
- [x] Draw order and culling: interior surfaces, terrain and objects inside interiors.
- [x] Interior haze distance.
- [x] Texture mipmaps for interiors, terrain, sky and shapes, and edge clamping on shapes.
- [x] Texture formats like the original, also for GUI bitmaps, with the unpacked texture options.
- [x] Screen brightness (gamma) for menus and the world.

### Shapes, weapons and effects

- [x] Shape loading and animation.
- [x] Players, shapes and effects drawn from a demo.
- [x] Lightning sway and rain streak size.
- [x] First-person weapon follows the player's pose, field of view and item commands.
- [x] Item images and other shapes placed by their shape root.
- [x] Weapon state machine on other players.
- [x] Explosion images face the camera.
- [x] Rocket trail (drawn once), its texture scroll in game time and rocket orientation.
- [x] Turret power animation.
- [x] Projectiles drawn fullbright and without haze.
- [x] Item and mine lights pulse and fade.
- [x] Shape lighting per vertex, also from far lights and the sun.

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
- [x] Weapon, compass, clock and sensor HUDs, the centre print box and a first-person-only crosshair.
- [x] Progress bar, formatted text layout (also in centre prints and help pop-ups), fonts, alignment, variables and line spacing.
- [x] Intro movie plays at start-up.
- [x] Main menu emblem animation.
- [x] Chat text without voice tags, server messages without a sender prefix.
- [x] HUD positions, crosshair size and scroll bars.
- [x] Loading screen palette and progress, and mission reloads.
- [x] Damage flash (with the original strength), chat layout, inventory header and command view column.
- [x] Objectives page on the command screen.
- [x] Chat display size, message fonts, command line, early messages, the command slot and chat only when the HUD has it.
- [x] Chat and command menu, drawn in full.
- [x] Compass readout, waypoint and arrow, and when the weapon HUD wakes.
- [x] Lists and menus leave keys to the game, like the original.
- [x] Crosshair target marks and labels for players and friendly objects, picked by their box.
- [x] Low resolution HUD layout and reticle.
- [x] Menu look: border and dialog frames, item text, text fields, sliders, key binding rows, combo boxes, the filter box and the buddy list.
- [x] Switch buttons ignore case.
- [x] Server browser with master lists, query results, pings, modal dimming and the server info dialog.
- [x] IRC history size.
- [x] Video options list the displays and modes.
- [x] Hover cursors.
- [x] Help pop-ups.
- [x] Player setup model with zoom limit and lighting.
- [x] No reticle, weapon list or sensor ping in the observer camera.
- [x] Scoreboard text position.
- [x] Text edit text, selection and cursor blink.
- [x] Scroll frame and its keyboard focus.
- [x] Text lists and list selection.
- [x] Labels resize to their text.
- [x] Sensor ping HUD.
- [x] Health display after death.

### Audio

- [x] Sound output and mixer.
- [x] Sound manager, sound commands and sounds in the world.
- [x] Player animation, sequence and turret power sounds.
- [x] Footstep sounds by surface.
- [x] Footstep, jet and weapon sounds move with the player.
- [x] Clean resampling to the device rate.

### Input and controls

- [x] Keyboard and mouse input routing.
- [x] Timed input scripts, chat and play view cursor.
- [x] Relative mouse mode changes only when needed.
- [x] Scripts can post actions to the play view.
- [x] Third-person view (with camera collision) and zoom.
- [x] Key maps are searched in the right order.
- [x] Mouse axis flip.

### Platforms

- [x] Builds for Windows, Linux and macOS on x64 and ARM64, and a per-user data folder for a read-only install.
- [x] Frame timing log, fewer allocations per frame and no stall on the first terrain draw.
- [x] Window size kept when changing resolution in a window.
