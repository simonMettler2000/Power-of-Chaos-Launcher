# Publication boundary

The private repository stores the launcher, not the games. Admitted paths are explicitly enumerated by `tools/publication_guard.py`: project-authored C#/Avalonia source, original vector UI assets, tests that generate synthetic fixtures, build configuration and documentation.

Excluded content includes original or patched game executables, archives, saves, cards/images/databases, music/voices, copied strings or proprietary dumps, compiled dependencies, credentials and personal installation paths. Mod packs and build products remain local and ignored. No copy-protection bypass belongs here.

Before publication, inspect each intended file, run the source-only guard and tests, stage only explicit paths, commit, then obtain an independent audit of the exact commit. Automated extension/signature checks supplement human review; they do not establish ownership by themselves.

Runtime state belongs in the user's local application-data directory, or an explicitly selected `POC_LAUNCHER_DATA_DIR` for testing. Adding a game path only reads metadata and stores that path locally; it does not upload or copy the installation.

Starting an enabled, supported startup mod may create a private modified executable in that runtime directory. It is derived locally from the user's installation and must never be staged, uploaded or bundled. Tracked mod code may contain factual target hashes, sizes, operand locations and small old/new numeric values, but no copied instructions or encoded executable payloads. Development tests use synthetic fixtures and fake processes; original and modified game executables are never run by tests.

Character-interaction profiles also contain factual function-pointer and relative call/branch target fields selecting existing code, and voice-cue numeric values. They may not contain original instruction sequences or newly encoded machine-code payloads. Width and original/modified whole-file hashes constrain every field; a target address is not permission to publish the referenced code.

The display component is acquired and built locally from separately pinned upstream inputs. Tracked files contain our authored build/setup scripts, maximize adaptation, generated-pattern test host and factual dependency metadata. The guard admits native/script files only at explicitly named tool paths. Downloaded source trees, upstream implementation, license files, archives, DLLs and compiler output remain excluded. Required notices accompany the locally prepared components and runtime copies. Source-only checks do not replace manual ownership and license review.

## Reviewed Windows downloads

The user's explicit distribution authorization permits separate GitHub Release assets containing the compiled launcher, its pinned self-contained .NET runtime and licensed dependencies. Source Git continues to exclude generated binaries and third-party payloads. The project's own code is MIT licensed; that license does not relicense the games or dependencies. Exact component and notice identities live in the authored release settings and component pins.

Every Windows ZIP must include the entire verified display bundle, dependency notices and the libgifcodec corresponding-source archive required by the selected MPL1.1 option. Source and notices remain in the release download, not hidden or discarded after build. All game executables, generated game patch caches, archives, saves, original/HD card images and personal settings remain excluded from downloads. Games are always supplied locally by each user.

Use the release build/test tools and the independent ZIP guard. A full pass executes only the authored package-check mode, with global .NET lookup disabled; it renders authored text to validate runtime, graphics and fonts without loading a game or user settings. Structural-only checks cannot establish an executable smoke pass. Two independent candidate reviews and a fresh audit of the exact source commit remain mandatory before publication. The source and asset inventories must bind to that commit.

Before making an existing private repository public, inspect every fetched branch and tag history with `tools/publication_guard.py --all-refs`, together with repository issues, pull requests and existing assets. The exact historical HD native source paths are admitted after source inspection; this does not make that development mod release-ready or admit arbitrary native content. Do not change or remove unrelated branch refs.
