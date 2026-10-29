# Power of Chaos Launcher

A lightweight, cross-platform desktop launcher for your own installations of **Yugi the Destiny**, **Kaiba the Revenge**, and **Joey the Passion**. Built with C# and Avalonia.

This repository contains launcher source and original vector UI assets. It does not contain, download or distribute the games or their executables, archives, saves, card databases, artwork, voices, music or mod packs.

## Windows download

Get the complete Windows ZIP from [Releases](https://github.com/simonMettler2000/Power-of-Chaos-Launcher/releases/latest). Choose **win-x64** for a 64-bit Windows PC, or **win-x86** for 32-bit Windows. Extract the entire ZIP into a new folder and open **PowerOfChaos.Launcher.exe**. Keep its other files beside it. The download includes the pinned .NET10 LTS runtime, application graphics/font dependencies and verified display components, so no separate .NET installation or mod setup is needed.

Choose **Settings → Add Game path**, then select an installed game folder or a parent containing the three games. Collection counts, original card images and localized names come from those local installations. The launcher does not upload your games, settings or saves. Windows10/11 editions supported by .NET10 are the target; Windows7/8.1 and native ARM64 game compatibility are not established.

The four available mods are **Skip Splash Screens**, **Skip character interactions**, **Original aspect ratio**, and **Temporary all cards**. Executable mods require the exact supported game revisions listed in [mod compatibility](docs/mods.md); finding a folder alone does not prove that revision is supported. Unknown revisions are refused rather than patched speculatively. Temporary all cards requires an existing unambiguous, writable shared save and the supported native card tables. Its switch must be changed with all games closed.

The stable v0.2.0 download keeps HD Cards, opponent-deck packs and guaranteed-new rewards disabled. This HD-Cards branch adds experimental large card previews with a separately supplied local pack and verified provider; **it may break or display incorrectly**. [HD setup and limits](docs/hd-launcher.md) explain the image-folder picker and help directly in Mods, the separate prepared-pack requirement, and automatic pack detection. Development/test builds are not part of the stable download. Future built-in mods are delivered through reviewed launcher updates; see [release and extension workflow](docs/releases.md).

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
- Read-only collection counts in Games and Collection, with a deduplicated total across all three games.
- Minimize-on-launch preference and a Wine runner setting for Linux/macOS.
- Local settings and atomic playtime checkpoints; no accounts, telemetry or always-on network service.
- Global **Skip Splash Screens**, **Skip character interactions** and **Original aspect ratio** switches for the three supported executable revisions, plus disabled entries for mods still in development.
- **Temporary all cards** on Windows: a backed-up shared save with three copies of every supported card, restored when unticked.

Playtime begins when this launcher starts tracking. It measures elapsed launcher sessions, including menus and idle time. Interrupted sessions retain their last saved checkpoint rather than counting time while the launcher was closed. Windows launches the selected executable directly; non-Windows launches through `wine start /wait /unix`. Actual game compatibility and Wine session behavior require testing on the chosen platform and runner.

Opponent-deck packs and guaranteed-new rewards remain disabled. Experimental HD large previews on this branch require a valid local pack and pinned provider; they do not replace small hand/field/deck-back/graveyard artwork. Native Linux/macOS ports of the games are not supplied.

## Collection

Games shows **owned/available** beside playtime and Play. Collection shows the same per-game counts and a unique-card total: **155** available in Yugi, **466** in Kaiba and **771** in Joey; **771** distinct cards overall. These are the full cumulative playable pools, including earlier games' cards, rather than only each opponent's new reward pool. Multiple copies and cards shared between games count once.

The stock games use a shared system.dat. On Windows the launcher looks for its registered CommonDir and ordinary common-folder locations, including the current user's VirtualStore when applicable. If it cannot find one unambiguous save, open Collection and browse to system.dat or paste its full path. Wine users can choose the file inside their Wine prefix. Only the selected path is saved in launcher preferences.

Collection reads never change saves, installed files or registry values. Counts refresh when opening Collection, activating the launcher, updating a tracked session or pressing Refresh. Missing, busy, damaged or unsupported saves display **—/total**, not a fabricated zero. A valid empty collection displays **0/total**. Only readable added games contribute to a partial unique count, which is labelled accordingly.

Expand a game's Collection panel to see **owned cards** and **missing cards**. The launcher reads the small original card images from your installed game files, so no HD card mod is required. Cards keep their native 49/50 × 72 size when clicked. Missing cards are dimmed; hovering shows the card name, number of copies and first game where it is available. Collapsing the panel releases its artwork from memory.

Use **Language**, beneath Settings, to choose English, German, French, Italian, Spanish or Japanese. The saved choice changes the launcher interface and card names immediately, including navigation, dialogs, settings, mod descriptions, tooltips and messages. Power of Chaos and the game titles remain unchanged. Names come from each installed game's own localized tables; the game language and files are unchanged. A card whose name is missing in the selected table retains its ID as a fallback.

Card pools and identities are read in memory from each installed game's own archive; no card database is bundled. The stock encrypted save's encoding, signature and both checksums are validated. Save identity acceptance by the games is not changed or inferred. Custom save formats and remapped card tables may be unavailable. See [collection format and validation](docs/collection.md).

## Temporary all cards

Close all three games, open **Mods**, and tick **Temporary all cards**. The launcher preserves your complete original `system.dat` before granting **three total copies** of each card in the added games' supported pools. With all three games added, the Collection counts become **155/155**, **466/466**, **771/771**, and **771/771 unique**. Each game retains its own available pool.

The mod stays active across launcher restarts. Close all games and untick it to restore the exact original `system.dat`, including original card quantities, decks, statistics and other fields. Changes made while active belong to the temporary profile; the last temporary save is archived separately before restoration. Other CommonDir files, including replay and exported deck files, are left alone.

The mod uses the automatically resolved save actually shared by the games. A different file chosen for read-only Collection inspection is never used as its write target. While active, Collection shows the temporary game save. Restore the original profile before changing game locations. The journal and retained backups live in `%LOCALAPPDATA%/PowerOfChaosLauncher/temporary-collection` on Windows; Mods displays the full local path. This directory deliberately ignores `POC_LAUNCHER_DATA_DIR` so ordinary launcher instances share one lock and recovery state.

Backup writes and transaction state precede each atomic save replacement. Interrupted or damaged transactions block Play until recovery; use **Restore original collection** when offered. Missing or damaged original backups are preserved and block restoration rather than being guessed. Closing the launcher never restores underneath a running game. The feature currently supports Windows; Wine save selection and process exclusion for this mod have not been established. Development checks use synthetic saves and fake games; actual game acceptance remains a manual test with the user's own installations. See [temporary collection transactions](docs/temporary-collection.md).

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

The complete Windows download includes the required verified display component. Source builds can acquire the pinned release inputs or rebuild it using [display setup and validation](docs/display.md). Missing or changed components produce an error before starting a game. Installed game files stay local and unchanged. Windows synthetic tests cover the display path; actual gameplay and Wine compatibility remain manual checks.

## Build and run

Source developers need the .NET10 SDK requested by `global.json` (currently10.0.403). The target runtime is pinned to10.0.14; full Windows downloads contain it. C#12 language semantics are retained. NuGet restores the committed dependency locks for Avalonia11.3.22 and its dependencies.

```sh
dotnet restore PowerOfChaos.Launcher.sln --locked-mode
dotnet build PowerOfChaos.Launcher.sln -c Release --no-restore
dotnet run --project src/PowerOfChaos.Launcher -c Release --no-build
```

Settings and counters are written under the current user's local application-data directory in `PowerOfChaosLauncher`. For isolated settings testing, set `POC_LAUNCHER_DATA_DIR` to a scratch folder before starting the launcher. The temporary-collection journal uses its fixed user-wide location; automated tests inject a private synthetic vault explicitly. Only activating **Temporary all cards** writes the shared game save.

Portrait artwork is loaded asynchronously from the selected installation and kept in memory. No original artwork is bundled, downloaded or written to the repository. Missing or unsupported portrait resources use a neutral placeholder and leave the game launch controls available.

## Verification

```sh
dotnet run --project tests/PowerOfChaos.Launcher.Tests -c Release
dotnet run --project tests/PowerOfChaos.Launcher.UiChecks -c Release
python tools/publication_guard.py --working-tree
```

The independent test harness creates synthetic fixtures, never copies real game data, and uses fake game processes. It covers depth limits, invalid files, persistence, cancellation, launch plans and session accounting. Headless UI checks exercise the actual Avalonia controls and render local screenshots without starting games. Optional `--installed` testing reads installed headers on Windows and never starts a game. Development and CI must not execute the original games.

On a machine with the supported installations, `dotnet run --project tests/PowerOfChaos.Launcher.Tests -c Release -- --installed --installed-mods` also verifies all three original and modified whole-file hashes entirely in memory. It writes no game copies and starts no games. UI checks accept `--installed-artwork` to read locally installed portraits and every native card mini for the three cumulative pools.

## Source-only publication

The publication guard admits a narrow set of authored text source/configuration paths. It rejects binaries, game-file extensions, unexpected paths, oversized files and suspicious embedded content. Run it against the working tree and exact commit before publishing. Explicitly stage inspected files; never force-add ignored content. Build products, runtime configuration, tests' generated fixtures and mod packs remain ignored.

The launcher requires separately obtained game installations. Private repository visibility does not authorize redistribution of game content. [Publication rules](docs/publication.md) describe this project's source-only boundary.

The authored launcher is [MIT licensed](LICENSE). Third-party dependencies keep their own licenses and notices. Complete Windows Release assets are separately inspected and include those notices and required corresponding library source; they never include game files or personal data.

## Platform scope

Avalonia supports Windows, Linux and macOS. The launcher source keeps discovery, settings and session logic separate from the desktop interface. Automated platform tests and real game compatibility tests are different: a passing launcher build does not prove the Windows games or future mods work through every Wine runner.

This is an independent launcher project, not an official KONAMI product.
