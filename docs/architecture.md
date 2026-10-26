# Architecture and roadmap

`PowerOfChaos.Launcher.Core` contains factual three-title descriptors, bounded filesystem discovery, atomic settings and event-based session tracking. It has no UI or game-engine dependency. `PowerOfChaos.Launcher` provides the Avalonia desktop interface and native folder picker. The test console exercises the core with synthetic files and fake processes.

The initial release discovers and launches local installations. Discovery checks expected filenames plus PE32/i386 and KCEJYUGI archive structures; these checks identify plausible installations, not authenticity or malware safety. Executable hashes are not fixed because legitimate versions and mods can differ. Launch remains an explicit user action.

Per-user settings contain search roots, discovered locations and preferences. Playtime is separate so settings changes cannot overwrite a session counter. The active monitor sleeps between infrequent durable checkpoints and waits asynchronously for process exit. Only one launcher-managed game session runs at a time.

Detected-game rows load their character portraits asynchronously from the installation's archive. A bounded read-only KCEJYUGI reader selects four fixed resource names, validates and decompresses their payloads, decodes uncompressed 24-bit BMP pixels and keys each selected image's exact green background to transparency (RGB 0,255,0 for Yugi/Kaiba; 0,255,6 for Joey). Yugi's eye layer is composed onto his original-size start portrait. No game code runs, no asset is bundled, and no image cache is written. Missing or unsupported artwork uses a neutral placeholder. Rescan/removal cancels old loads; only a still-current row may receive a result, and native bitmaps are disposed when rows retire or the window closes.

Future work: independently validate the stock save formats for read-only ownership statistics; design reversible shared/isolated profiles with acquisition provenance; add signed/hashed compatible mod manifests and conflict checks. Each mod must have a real implementation and tested game-version compatibility before its switch can be enabled. Archive mods should compose individual resource changes against a clean baseline rather than repeatedly overwrite whole modified archives.

Cross-platform UI does not port the games. Windows uses the native executable; Linux/macOS use an explicitly configured Wine executable. Runner-specific process lifetime, saves/registry prefixes and mod loading need platform tests.
