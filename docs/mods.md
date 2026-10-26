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
