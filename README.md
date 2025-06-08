# OpenTribes

OpenTribes is a reimplementation of Starsiege: Tribes (version 1.11) in C#. The dedicated server is done and the
game client is being built.
It uses the data files from your own copy of the game. No game data is included here.

## What you need

- The .NET 10 SDK.
- SDL3. On Linux install your distribution's `SDL3` package. On Windows and macOS put `SDL3.dll` or
  `libSDL3.0.dylib` next to the `tribes` executable, or install it system-wide.
- A graphics driver with OpenGL 3.3.
- An installed copy of Tribes 1.11. The folder that contains `base/` is the game directory used below.

## Build

    dotnet build OpenTribes.sln
    dotnet publish OpenTribes.sln -o out

Publishing puts the programs in `out/`:

| Program | What it is |
|---|---|
| `tribes` | The game client |
| `tribes-server` | The dedicated server |
| `tribes-netclient` | A headless client for testing connections and reading `.rec` demos |
| `scriptrun` | Runs a Tribes console script outside the game |

## Run a server

    out/tribes-server --root /path/to/Tribes -- -dedicated Broadside

Replace `Broadside` with any mission name. The server listens on UDP port 28001 by default (`$Server::Port` in the
server scripts). Run `out/tribes-server` with a wrong option to see all its options. The main ones are `--port`,
`--hostname`, `--password` and `--max-players`.

## Run the client

The client is not playable yet.
It can show a bitmap from the game files:

    out/tribes --root /path/to/Tribes --show bg.bmp

## Run the tests

    dotnet test OpenTribes.sln

Some tests currently fail because their data files are in the C++ repository. See [TODO.md](TODO.md).

## Status

The server is playable. The client is in progress. Known gaps, done and open, are listed in [TODO.md](TODO.md).
