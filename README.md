# OpenTribes

OpenTribes is a reimplementation of Starsiege: Tribes (version 1.11) in C#. It has a dedicated server and a game
client, and runs on Linux, Windows and macOS.
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

The client writes settings into the game directory (`config/`, `temp/`), so point it at a copy of your install if you
want to keep the original untouched.
If the game directory is read-only, the client keeps its files in a per-user folder instead.

    out/tribes --root /path/to/Tribes --windowed

This starts the stock menus. To join a server directly:

    out/tribes --root /path/to/Tribes --windowed +connect 127.0.0.1:28001

Useful options:

| Option | Effect |
|---|---|
| `--windowed` | Stay in a window, even when the game settings ask for full screen |
| `--no-audio` | Start without sound |
| `--allow-remote` | Allow connecting to servers outside this machine. Without it the client only talks to 127.0.0.1 |
| `--allow-browser` | Let in-game links open in your web browser |
| `+connect HOST:PORT` | Join that server at start-up |
| `-mod NAME` | Load a mod folder |

The master server list is empty, so join servers by address.

## Run the tests

    dotnet test OpenTribes.sln

Some tests currently fail because their data files are in the C++ repository. See [issue #18](https://github.com/victorprocure/OpenTribes/issues/18).

## Builds for other platforms

    dotnet publish OpenTribes.Client -c Release -r win-x64 --self-contained -o dist/win-x64

Other targets are `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`. Copy the SDL3 library for that
platform into the output folder. macOS builds are not signed. After downloading one, run
`xattr -dr com.apple.quarantine <folder>` before starting it.

Only Linux x64 and Windows x64 (under Wine) have been tried so far.

## Status

The server and client are playable. Open gaps are tracked in [GitHub issues](https://github.com/victorprocure/OpenTribes/issues). [TODO.md](TODO.md) is a frozen list of what was done and open on 2026-11-05.
