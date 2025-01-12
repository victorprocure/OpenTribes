# OpenTribes

OpenTribes is a reimplementation of Starsiege: Tribes (version 1.11) in C#. The dedicated server is being built
first. A game client comes after it.
It uses the data files from your own copy of the game. No game data is included here.

## What you need

- The .NET 10 SDK.
- An installed copy of Tribes 1.11. The folder that contains `base/` is the game directory used below.

## Build

    dotnet build OpenTribes.sln
    dotnet publish OpenTribes.sln -o out

Publishing puts the programs in `out/`:

| Program | What it is |
|---|---|
| `tribes-server` | The dedicated server |
| `scriptrun` | Runs a Tribes console script outside the game |

## Run a server

The server program starts, but it cannot host a game yet. Run `out/tribes-server` with a wrong option to see its
options.

## Run the tests

    dotnet test OpenTribes.sln

Some tests currently fail because their data files are in the C++ repository. See [TODO.md](TODO.md).

## Status

The server is in progress. Known gaps, done and open, are listed in [TODO.md](TODO.md).
