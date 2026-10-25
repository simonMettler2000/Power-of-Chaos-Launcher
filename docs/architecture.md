# Architecture and roadmap

`PowerOfChaos.Launcher.Core` contains factual three-title descriptors, bounded filesystem discovery, atomic settings and event-based session tracking. It has no UI or game-engine dependency. `PowerOfChaos.Launcher` provides the Avalonia desktop interface and native folder picker. The test console exercises the core with synthetic files and fake processes.

The initial release discovers and launches local installations. Discovery checks expected filenames plus PE32/i386 and KCEJYUGI archive structures; these checks identify plausible installations, not authenticity or malware safety. Executable hashes are not fixed because legitimate versions and mods can differ. Launch remains an explicit user action.

Per-user settings contain search roots, discovered locations and preferences. Playtime is separate so settings changes cannot overwrite a session counter. The active monitor sleeps between infrequent durable checkpoints and waits asynchronously for process exit. Only one launcher-managed game session runs at a time.

Future work: independently validate the stock save formats for read-only ownership statistics; design reversible shared/isolated profiles with acquisition provenance; add signed/hashed compatible mod manifests and conflict checks. Each mod must have a real implementation and tested game-version compatibility before its switch can be enabled. Archive mods should compose individual resource changes against a clean baseline rather than repeatedly overwrite whole modified archives.

Cross-platform UI does not port the games. Windows uses the native executable; Linux/macOS use an explicitly configured Wine executable. Runner-specific process lifetime, saves/registry prefixes and mod loading need platform tests.
