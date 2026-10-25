# Launcher source-only project

Preserve installed games as read-only inspection inputs. Never run original game executables during development or automated tests. Own authored launcher executables and synthetic/fake-process tests may be run.

Only launcher-authored text source, configuration, documentation and original vector UI assets belong in Git. Never add game executables, archives, saves, card databases, artwork/audio, extracted proprietary data, raw disassembly, reconstructed game code or third-party mod packs. Do not hide such material inside text or generated source. Runtime installation paths and personal settings stay outside the repository.

Use independent review before publishing. Only the coordinator stages explicit inspected paths, commits and pushes. Run `tools/publication_guard.py` on the working tree and exact commit. Audit that exact commit independently before pushing. Private visibility does not change the source-only publication rule.

Discovery must remain read-only, cancellable and bounded. Do not follow directory links outside the selected tree. Tests generate synthetic PE/archive headers, not copies of game bytes. Preserve launcher settings and playtime atomically; do not modify installed files or game saves. Mark unavailable mods honestly.
