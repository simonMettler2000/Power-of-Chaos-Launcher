# Publication boundary

The private repository stores the launcher, not the games. Admitted paths are explicitly enumerated by `tools/publication_guard.py`: project-authored C#/Avalonia source, original vector UI assets, tests that generate synthetic fixtures, build configuration and documentation.

Excluded content includes original or patched game executables, archives, saves, cards/images/databases, music/voices, copied strings or proprietary dumps, compiled dependencies, credentials and personal installation paths. Mod packs and build products remain local and ignored. No copy-protection bypass belongs here.

Before publication, inspect each intended file, run the source-only guard and tests, stage only explicit paths, commit, then obtain an independent audit of the exact commit. Automated extension/signature checks supplement human review; they do not establish ownership by themselves.

Runtime state belongs in the user's local application-data directory, or an explicitly selected `POC_LAUNCHER_DATA_DIR` for testing. Adding a game path only reads metadata and stores that path locally; it does not upload or copy the installation.
