# Power of Chaos Launcher

A lightweight, cross-platform desktop launcher for your own installations of **Yugi the Destiny**, **Kaiba the Revenge**, and **Joey the Passion**. Built with C# and Avalonia.

This repository contains launcher source and original vector UI assets. It does not contain, download or distribute the games or their executables, archives, saves, card databases, artwork, voices, music or mod packs.

## First run

The library starts with **no game path added**. Click **Settings** beneath **About** in the sidebar, then **Add Game path**. Browse to a folder or paste its path. You can select a single game's directory or a parent directory containing all three installations.

The launcher searches the selected folder and up to **six directory levels below it**, with cancellation and traversal limits. It recognizes the original executable names alongside structurally valid `Data.dat` and `Voice.dat` archives. Executable and archive headers are inspected without starting the games. An unrelated or incomplete folder produces **game not found**; existing library entries are retained.

Only discovered games appear in the library. Locations and preferences persist between launches. You can rescan or remove launcher entries without deleting installed game files.

## Current capabilities

- Black desktop interface with compact game rows and monochrome controls; no game artwork is bundled.
- An original gold pyramid-and-eye launcher icon, generated at multiple desktop sizes from the included SVG during build. Generated icon binaries remain outside Git.
- Character icons are read from each detected game's local archive: Kaiba's start pose, Joey's reaction pose, and Yugi's start pose with the open-eye layer. Green backgrounds become transparent in memory.
- Cogwheel settings menu, folder browser and pasteable game paths.
- Discovery of one or all three titles, including nested installations.
- Per-game launching and recorded session playtime.
- Minimize-on-launch preference and a Wine runner setting for Linux/macOS.
- Local settings and atomic playtime checkpoints; no accounts, telemetry or always-on network service.
- A mod roadmap that accurately marks features still in development.

Playtime begins when this launcher starts tracking. It measures elapsed launcher sessions, including menus and idle time. Interrupted sessions retain their last saved checkpoint rather than counting time while the launcher was closed. Windows launches the selected executable directly; non-Windows launches through `wine start /wait /unix`. Actual game compatibility and Wine session behavior require testing on the chosen platform and runner.

Collection statistics, reversible shared/isolated collections, opponent-deck packs, guaranteed-new rewards, skipped voice waits and HD artwork are **not implemented in this first version**. Their mod entries are disabled rather than pretending to apply a modification. Native Linux/macOS ports of the games are not supplied.

## Build and run

Install the .NET 8 SDK. `global.json` requests 8.0.400 with feature-band roll-forward. NuGet restores Avalonia 11.3.22 and its dependencies.

```sh
dotnet restore PowerOfChaos.Launcher.sln
dotnet build PowerOfChaos.Launcher.sln -c Release --no-restore
dotnet run --project src/PowerOfChaos.Launcher -c Release --no-build
```

Settings and counters are written under the current user's local application-data directory in `PowerOfChaosLauncher`. Nothing is saved inside an installed game directory. For isolated testing, set `POC_LAUNCHER_DATA_DIR` to a scratch folder before starting the launcher.

Portrait artwork is loaded asynchronously from the selected installation and kept in memory. No original artwork is bundled, downloaded or written to the repository. Missing or unsupported portrait resources use a neutral placeholder and leave the game launch controls available.

## Verification

```sh
dotnet run --project tests/PowerOfChaos.Launcher.Tests -c Release
dotnet run --project tests/PowerOfChaos.Launcher.UiChecks -c Release
python tools/publication_guard.py --working-tree
```

The independent test harness creates synthetic fixtures, never copies real game data, and uses fake game processes. It covers depth limits, invalid files, persistence, cancellation, launch plans and session accounting. Headless UI checks exercise the actual Avalonia controls and render local screenshots without starting games. Optional `--installed` testing reads installed headers on Windows and never starts a game. Development and CI must not execute the original games.

## Source-only publication

The publication guard admits a narrow set of authored text source/configuration paths. It rejects binaries, game-file extensions, unexpected paths, oversized files and suspicious embedded content. Run it against the working tree and exact commit before publishing. Explicitly stage inspected files; never force-add ignored content. Build products, runtime configuration, tests' generated fixtures and mod packs remain ignored.

The launcher requires separately obtained game installations. Private repository visibility does not authorize redistribution of game content. [Publication rules](docs/publication.md) describe this project's source-only boundary.

## Platform scope

Avalonia supports Windows, Linux and macOS. The launcher source keeps discovery, settings and session logic separate from the desktop interface. Automated platform tests and real game compatibility tests are different: a passing launcher build does not prove the Windows games or future mods work through every Wine runner.

This is an independent launcher project, not an official KONAMI product.
