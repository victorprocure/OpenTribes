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

## Done

### Server

- [x] Networking core and the server program. Oversized packets are cut like the original.
- [x] Console script language: lexer, parser, interpreter, built-in functions, math commands and number formatting.
- [x] Client command table, client commands and key table.
- [x] Ban list and console log.
