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
- Global **Skip Splash Screens**, **Skip character interactions** **Original aspect ratio** and **HD card previews** switches for the three supported executable revisions, plus disabled entries for mods still in development.

Playtime begins when this launcher starts tracking. It measures elapsed launcher sessions, including menus and idle time. Interrupted sessions retain their last saved checkpoint rather than counting time while the launcher was closed. Windows launches the selected executable directly; non-Windows launches through `wine start /wait /unix`. Actual game compatibility and Wine session behavior require testing on the chosen platform and runner.

Collection statistics, reversible shared/isolated collections, opponent-deck packs and guaranteed-new rewards are **not implemented**. Their mod entries are disabled rather than pretending to apply a modification. Native Linux/macOS ports of the games are not supplied.

The **HD-Cards** branch adds a Windows [HD card preview candidate](docs/hd-launcher.md). HD mode defaults to GDI for Yugi, Kaiba and Joey and smoothly scales locally supplied full 800 x 1160 RGB8 masters using GDI HALFTONE only when a complete large 200 x 290 preview matches at one of nine explicit positions across duel, Card List and Deck Construction screens. Small cards, rotated hand cards, field cards, deck backs and graveyard cards keep their original graphics.

Generate a local pack from an accepted delivery manifest and your local original-card references, choose that pack folder in **Settings > Preferences**, and supply the separately built [HD display component](tools/HdCardsComponent/README.md). The folder contains `yugi-565.tsv`, `kaiba-565.tsv` and `joey-565.tsv`, which refer to external local reference/master files that must remain available. Masters must be metadata-free, non-interlaced RGB8 PNGs containing only IHDR, consecutive nonempty IDAT and IEND chunks. The pack builder excludes 199-pixel-wide originals rather than inventing missing padding. Packs, artwork, native components and third-party source remain local and are not supplied by this repository.

The GDI decoder obtains owned BGRA pixels directly from the shared strict PNG decoder, without COM/WIC on that path. The full master is scaled to the physical preview with HALFTONE filtering.

The optional V3 local pack adds a Deck Construction flip candidate. It proves
the complete current preview as one supplied original background plus a supported
horizontal squeeze/alpha pose and optional verified normal cursor, then renders
the full HD master smoothly at that pose. Unknown or ambiguous pixels retain the
original display. It does not broaden small-card or other animation support.
See the component's V3 profile contract.

On 2026-10-29, the R5 GDI build was visually checked in prepared local copies of all three games: Duel, Deck Construction and Card List large previews, windowed/fullscreen changes, hovering over previews, card switches, maximize/restore and normal exit. Supported previews remained HD in the settled fullscreen views, using the current serif description/effect masters. Two independent source/headless reviews also passed. These are sampled Windows display checks, not exhaustive card, animation or hardware coverage. Some immediate resize/fullscreen captures showed the old viewport before the next settled frame; unsupported overlays can temporarily retain original artwork. Small cards remain unchanged. See the [component scope and validation limits](tools/HdCardsComponent/README.md) and [pack and renderer foundation](docs/hd-cards.md).

## Skip Splash Screens

Open **Mods** and tick **Skip Splash Screens**. The same setting applies to Yugi, Kaiba and Joey. On the next launch, the publisher logos are skipped and the normal title menu appears with its entrance fade and music. Untick it to restore normal startup on the next launch. Changing the setting does not rewind a game already running.

The launcher verifies the installed executable against an explicitly supported SHA256 before preparing a local modified copy. The installed executable, archives and saves are not patched. Unsupported versions produce an explanation rather than receiving a speculative patch; untick the mod to launch them normally. Only numeric startup fields are changed; no game content is bundled or downloaded.

The local copy lives in the launcher's application-data directory, with assets still read from the installation. Static byte checks, synthetic process tests and UI checks cover the implementation; game startup is not executed during development. Actual rendering, copy-location assumptions and Wine compatibility still need a manual test with your own installation. See [mod behavior and supported versions](docs/mods.md).

## Skip character interactions

Tick **Skip character interactions** in **Mods** to skip the duel-start greeting and in-duel character dialogue, reaction cut-ins and associated waits. Duel music, card sounds, normal card effects and gameplay continue. The end-of-duel result presentation is retained. Skipping cosmetic reactions also skips their random variant choices; the exact sequence from an identical random seed may therefore differ.

The mods work independently or together, using a separately verified local executable for each enabled combination. Unticking one restores that behavior on the next launch while keeping the other selections. No installed executable, archive or save is modified. The same supported-input hashes and development no-execution limits apply.

## Original aspect ratio

The three inspected games use **800 x 600**, a **4:3** picture. Tick **Original aspect ratio** in **Mods** to start in an 800 x 600 window. Dragging a window edge keeps that ratio. Maximize fills the monitor work area, and borderless fullscreen fills the monitor; the game picture stays centered with black bars wherever needed. **Alt+Enter** toggles borderless fullscreen. Unticking the mod restores the ordinary display path on the next launch.

The mod preserves the game's existing timing and adds no frame-rate selection. Resizing can temporarily pause presentation while the window is being dragged. The game's own short catch-up behavior remains unchanged.

This option needs a separately prepared, verified local display component. [Display setup and validation](docs/display.md) explains how to build it and put it beside your launcher. Missing or changed components produce an error before starting a game. The repository supplies authored build tools and factual pins; compiled components and game files stay local. Windows synthetic tests cover the display path; actual gameplay and Wine compatibility remain manual checks.

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

On a machine with the supported installations, `dotnet run --project tests/PowerOfChaos.Launcher.Tests -c Release -- --installed --installed-mods` also verifies all three original and modified whole-file hashes entirely in memory. It writes no game copies and starts no games. UI checks accept `--installed-artwork` to read locally installed portraits.

## Source-only publication

The publication guard admits a narrow set of authored text source/configuration paths. It rejects binaries, game-file extensions, unexpected paths, oversized files and suspicious embedded content. Run it against the working tree and exact commit before publishing. Explicitly stage inspected files; never force-add ignored content. Build products, runtime configuration, tests' generated fixtures and mod packs remain ignored.

The launcher requires separately obtained game installations. Private repository visibility does not authorize redistribution of game content. [Publication rules](docs/publication.md) describe this project's source-only boundary.

## Platform scope

Avalonia supports Windows, Linux and macOS. The launcher source keeps discovery, settings and session logic separate from the desktop interface. Automated platform tests and real game compatibility tests are different: a passing launcher build does not prove the Windows games or future mods work through every Wine runner.

This is an independent launcher project, not an official KONAMI product.


Optional normal software cursor previews (candidate)

The GDI cursor extension uses a local V2 pack. It preserves the exact whole-card
recognition rule by explaining every preview pixel as reference plus one verified
normal cursor, then placing that cursor above the HD master. Unknown cursor
states/layers and ambiguous matches retain the base. Other cards outside the
large preview remain outside this feature. Independent review and actual hover
checks are still required; previous screenshots retain their original DLL pins.

Use `python tools/hd_cards_add_cursor_bank.py --pack <existing-V1-pack> --output
<fresh-V2-folder> --archive yugi=<absolute-data.dat> --archive kaiba=<absolute-Data.dat>
--archive joey=<absolute-data.dat>` with explicit read-only original archive paths.
This writes the three RGB565 V2 configs and two normal64x64 banks per game into
a fresh local folder, leaving the old pack and archives untouched. Select that
folder in Settings only after qualifying the candidate component. Generated
banks contain original game pixels and must remain local, outside source control.
V1 packs still use the original unoccluded-preview path. D3D9 retains V1 support;
V2 cursor-aware presentation is GDI-only.

Cursor-aware presentation requires a viewport at least800x600. At smaller sizes,
it displays the same owned original base, including its cursor; GDI modes1/2 can
combine or delete reduction samples, so a single foreground texel is unproved.
V1 and unoccluded exact-card minification keep their existing behavior.

Opaque cursor foreground uses the RGB565-to-RGB8 bit replication verified
against owned GDI conversion of all65536 words, preserving native base colors
at preview crossings. Partial alpha still uses separately floored q/15 terms
against the new physical HD background; it is not a retained old-card pixel.
