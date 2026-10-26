# Publication boundary

The private repository stores the launcher, not the games. Admitted paths are explicitly enumerated by `tools/publication_guard.py`: project-authored C#/Avalonia source, original vector UI assets, tests that generate synthetic fixtures, build configuration and documentation.

Excluded content includes original or patched game executables, archives, saves, cards/images/databases, music/voices, copied strings or proprietary dumps, compiled dependencies, credentials and personal installation paths. Mod packs and build products remain local and ignored. No copy-protection bypass belongs here.

Before publication, inspect each intended file, run the source-only guard and tests, stage only explicit paths, commit, then obtain an independent audit of the exact commit. Automated extension/signature checks supplement human review; they do not establish ownership by themselves.

Runtime state belongs in the user's local application-data directory, or an explicitly selected `POC_LAUNCHER_DATA_DIR` for testing. Adding a game path only reads metadata and stores that path locally; it does not upload or copy the installation.

Starting an enabled, supported startup mod may create a private modified executable in that runtime directory. It is derived locally from the user's installation and must never be staged, uploaded or bundled. Tracked mod code may contain factual target hashes, sizes, operand locations and small old/new numeric values, but no copied instructions or encoded executable payloads. Development tests use synthetic fixtures and fake processes; original and modified game executables are never run by tests.
