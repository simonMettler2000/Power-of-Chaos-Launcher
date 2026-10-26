# Mods

The checkbox at the left of each row controls that mod for all three games. A title and description follow it. Disabled rows describe features still in development.

## Skip Splash Screens

Tick the switch before pressing Play. The mod skips Konami and the other publisher logos in the normal startup sequence and continues into the normal title/menu entrance animation. It retains the title music, menu options and input setup. It does not skip later game-requested video sequences.

Unticking the switch uses the original executable on the next launch. There is no continuing background hook to unload once the startup sequence has finished. An already-running game is left alone. The setting is saved between launcher sessions, independently of which games are currently installed.

### Supported inputs

The following SHA256 values identify the exact inspected executable revisions. Filenames and discovery headers alone do not establish support for a mod.

| Game | Executable | Original SHA256 |
|---|---|---|
| Yugi the Destiny | `yugi_pc.exe` | `1e5d81b1e44edf09e91addab582ef963694aa285e4be5eb5ad8e0ce4ab04664c` |
| Kaiba the Revenge | `kaiba_pc.exe` | `a829b16eee11356159850029f5a809886b47c33498ce1d849dd1591a603c0309` |
| Joey the Passion | `joey_pc.exe` | `08018ebbc73f3fa51fdf220e903f69cd9a22572e508ea9cd145853abf8ed2dda` |

The inspected Joey input has a stale PE SizeOfCode field; its retail provenance has not been established. Support here identifies those exact bytes, not a claim that this is a canonical retail release. Alternate executables and other regional or modified revisions require separate inspection. A refusal affects only the modded launch: untick the switch to use the ordinary launch path.

### Local preparation

For a supported input, the launcher changes the initial logo timer and index in a private copy. The first logo update follows the existing completion path before drawing a logo, and the ordinary scene transition initializes the title menu. It does not redirect a scene factory, inject code, replace assets, alter saves or change copy-protection logic.

The executable copy is kept under the launcher's local application-data directory. Game archives remain in their installation, which is used as the working directory. Preparation verifies the original and resulting hashes and refuses an unexpected version or damaged cache. No game executable or binary patch is distributed by this repository.

### Verification limits

Development checks inspect installed inputs as data, verify patch results in memory, and exercise the launcher with synthetic fixtures and fake process runners. UI tests operate the actual Avalonia controls without pressing Play. Neither original nor modified games are executed during development or automated testing.

Actual logo-free startup, audio/rendering, module-location assumptions, operating-system permissions and Wine behavior therefore remain manual compatibility checks. A passing launcher build or exact patch hash cannot substitute for running your own installation. The native title fade is deliberately retained and may still take a short time before menu options appear.

## Skip character interactions

This global switch skips the initial character greeting and the character interruptions during duels: spoken reactions, portrait/special-monster reaction cut-ins and the pauses associated with those presentations. It keeps ordinary duel music, card sound effects and normal card animations, including fusion visuals and their normal music behavior. The end-of-duel win/loss/draw presentation is retained.

The implementation bypasses optional character event callbacks and uses native skip/continuation branches before creating queued presentations. Standalone voice requests and fusion voice cues receive separate treatment where needed. These are distinct paths in each game; the profiles are individually derived for the three exact executable hashes listed above. No game code, sound file or modified executable is bundled. Existing code is selected by factual pointer/relative-target metadata; no instruction payload is inserted.

Skipping the optional character callbacks also removes random choices for their cosmetic variants. Rules, decks, rewards and saved collections are not patched, but the exact later random sequence from an identical seed is not guaranteed to remain the same.

| Splash screens | Character interactions | Next launch |
|---|---|---|
| Off | Off | Original executable |
| On | Off | Skip publisher logos; normal character presentations |
| Off | On | Normal publisher logos; skip greeting and in-duel character interruptions |
| On | On | Both modifications |

Each enabled combination has its own full-file verification and isolated cache identity. Turning off one mod keeps the other selected. Turning both off returns to the unchanged original executable when Original aspect ratio is also off. Changes take effect on the next launch and do not patch a running game.

Automated checks exercise the four combinations with synthetic fixtures and fake processes, and verify the real pinned outputs entirely in memory. They never execute original or modified games. Actual duel behavior, audio/rendering and Windows/Wine compatibility remain manual checks under the project's no-execution rule.

## Original aspect ratio

This global switch uses the same supported executable hashes above. All three inspected targets request an 800 x 600 picture. It starts windowed at that size, constrains Windows drag resizing to 4:3, and centers the scaled picture with black bars when the window fills the work area or the monitor. The maximum button uses a custom workarea fill/restore operation; it does not set the Windows native maximized state bit. Alt+Enter toggles borderless fullscreen.

All three switches are independent and apply on the next launch. With only Original aspect ratio enabled, the private executable has exactly the original bytes. With either of the other mods enabled, its bytes match that combination's existing verified output. Display DLLs and a fresh global-only configuration live beside that private executable; archives are still read from the original working directory. No DLL, INI or executable is written into the installation.

The wrapper's game-logic limiter is disabled. The game's native timing fields and arguments are unchanged; its normal limiter defaults to 30, while the existing game option `-speedy` selects 60. This mod adds neither that argument nor a new speed selector. Presentation refresh and game updates remain separate, so a future speed mod can address game timing independently. Animation can stop during interactive drag resizing, and native catch-up after a short delay is preserved.

Turning this mod off selects the original or binary-only cache path. The launcher refuses an aspect-off executable directory containing either display proxy DLL, so a stale display cache cannot silently keep the component active. Unsupported executable versions, missing components and invalid caches fail before starting a process.

See [display setup](display.md) for component provenance, local prerequisites, tests and platform limits. A passing synthetic host proves only the exercised DirectDraw calls; it does not prove every game scene, physical device, graphics driver or Wine runner.
