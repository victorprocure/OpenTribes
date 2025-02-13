# TODO

Work left and work finished on OpenTribes, compared with the original Tribes 1.11.

This file is updated in the same commit as each fix or feature: the item moves to Done, and new gaps found along the
way are added to Open.

## Open

### Server

- [ ] The simulation layer and world data (terrain, interiors, shapes, collision).
- [ ] Datablocks and game objects.
- [ ] Hosting a mission from the game directory.
- [ ] Players, items, projectiles, vehicles and turrets.
- [ ] Bots (AI players).
- [ ] Master server heartbeat.

### Client

- [ ] The game client.

### Project

- [ ] Two `WorldTests` vector tests fail because their data files are in the C++ repository (`OpenTribes-cpp/tests`).

## Done

### Server

- [x] Networking core and the server program, with the telnet console and server commands. Oversized packets are cut like the original.
- [x] Console script language: lexer, parser, interpreter, built-in functions, math commands and number formatting.
- [x] Client command table, client commands and key table.
- [x] Ban list and console log.
- [x] `scriptrun` runs console scripts outside the game.
- [x] Numbers round toward zero like the original: stored floats, parsing and arithmetic. Overflow gives infinity.
- [x] Simulation layer: sim manager, number helpers, text scanning, string table and world code, used by `scriptrun` and the server.
- [x] Datablocks, their tables and their network packing.
- [x] Game commands: backlog, client game commands, command map and game commands.
- [x] Game objects: control object, damage, displacement and game state.
- [x] Game connection and ghost manager.
