# Windows downloads and future built-in mods

Stable downloads are self-contained win-x64 and win-x86 ZIPs. They include the launcher, pinned .NET10 LTS runtime, managed/native application dependencies, the verified x86 display bundle and HD card component bundle for the games, complete redistribution notices and the required unmodified GIF codec corresponding source. Users extract everything together, open the launcher and select their existing game folders. They do not build a component or install an SDK.

The installed games remain user-provided inputs. Executable version support is explicit, not inferred from filenames. The collection reader uses each installation's own card tables, images and names; no game database or artwork is distributed. HD card pictures are a separate download that the user chooses; the launcher download holds only the component that draws them. Temporary cards modifies only the user-activated backed-up shared save and restores it exactly on deactivation. No copyrighted game payload, game-derived patch cache or save belongs in any download.

## Build a reviewed package

Start from a clean, independently audited main commit. `global.json`, `Directory.Build.props`, dependency lockfiles and `tools/Release/release-settings.json` pin SDK, runtime and input identities. Update SDK/runtime together when applying a supported security patch; do not treat an old self-contained runtime as automatically updated by the user's machine. The current framework support end date is recorded in release settings.

On Windows with PowerShell7 and the pinned SDK, acquire inputs into a new directory outside the source checkout:

```powershell
./tools/Release/Acquire-ReleaseInputs.ps1 -Destination C:/PoCRelease/inputs
./tools/Release/Build-WindowsPackage.ps1 -Version 0.3.2 -RuntimeIdentifier win-x64 -SourceCommit <audited-commit> -DisplayComponentsDirectory C:/PoCRelease/inputs/original-aspect-ratio-v1 -HdComponentsDirectory C:/PoCRelease/inputs/hd-card-artwork-v2 -NoticesDirectory C:/PoCRelease/inputs/notices -PackageOutputDirectory C:/PoCRelease/x64
```

Use another new output directory for win-x86. A private SDK can be supplied with `-DotNetPath`; no machine-wide installation is needed for a maintainer's build. Clean-checkout builds bind the exact Git commit. An exported committed snapshot can instead use a private source manifest with sourceCommit, sourceRepositoryDirectory and all path/length/SHA256 entries, verified against the original Git objects. Dirty snapshots cannot impersonate an audited release.

The builder preserves the full publish dependency graph, uses locked restore, avoids trimming/single-file behavior, embeds version/commit, omits debug artifacts, verifies every display/HD component/notice/source pin and writes deterministic ZIP entry order/timestamps plus a complete file/dependency manifest and SHA256 sums. Each output directory must be new; installed game directories are never an output target.

Run `Test-WindowsPackage.ps1` for each ZIP with its expected version, commit and RID, then `python tools/release_guard.py --package <zip> --expected-commit <sha>`. Validation extracts safely into new owned storage and runs only the authored `--package-check` mode with global .NET roots/hooks disabled. That mode verifies local runtime location, renders authored text to exercise desktop graphics/fonts, checks native component identities, and creates a new diagnostic JSON file without constructing the main window, loading user settings or inspecting a game. Structural-only validation is explicitly weaker and cannot stand in for a full smoke pass. The tests cannot establish actual proprietary gameplay compatibility under the project's no-game-execution rule.

## Publish and maintain

Obtain two independent passing candidate reviews, full canonical checks and a fresh audit of the exact committed source. Dispatch **Windows download release** on `main` with the version in `Directory.Build.props` and the source audit confirmation. The build jobs have read-only contents permission. After both architecture packages pass, the final job uses contents-write permission to prepare a draft GitHub release with the audited commit as its target.

Audit the final packages from that workflow run, including their commit, both ZIP hashes, smoke results and published asset inventory. Publish the draft from GitHub after those checks pass. Keep the launcher release marked as the latest stable release so the README download link follows it. Existing version tags and releases are never overwritten. Builds acquire their pinned public assets and upstream notices automatically.

The HD card component archive is written by `tools/HdCards/Build-HdComponent.ps1 -BundleArchive` for the pinned provider only, published once under its own tag (not marked as the latest release) and pinned by name, length and SHA256 in release settings. A changed provider gets a new pin, a new archive and a new tag; a published archive is never replaced. The tag and the archive carry the bundle profile and a build number: `hd-card-components-v2` went with launcher 0.3.0, `hd-card-components-v2.1` with 0.3.1. The bundle directory inside the download stays `Components/hd-card-artwork-v2`. The release workflow downloads that archive and fails when it is not public, so the archive is published before the workflow is dispatched. A candidate built before then passes the maintainer's copy to `Acquire-ReleaseInputs.ps1 -LocalHdComponentsArchive`; it must be the pinned file by name, length and SHA256, and it is expanded and checked against the per-file pins exactly as a downloaded one.

The Git repository remains source-only even when public. The package/source guards are separate: a DLL is allowed only in the reviewed download, and a game executable is allowed in neither. All existing branch/tag histories must pass the source inspection before an existing private repository becomes public. Preserve corresponding source and notices with the binaries according to their terms.

## Adding a future built-in mod

Implement the mod in the launcher source and its normal launch preparation/lifecycle, add translated controls and honest availability state, and add meaningful synthetic/read-only verification. Extend supported executable profiles only after inspecting each exact input; do not guess patch offsets or copy original instruction payloads into source. A mod that needs a new native dependency must add a reviewed build recipe, exact dependency/notice/source pins, package checks and a stable acquisition source. Original game/HD assets remain local user inputs.

Normal SDK publication already includes new managed code, resources and project/package dependencies. The release scripts validate the resulting dependency graph and package every reviewed required component; new mods do not need their own release branch or a separate end-user installer. Update the package-check mod inventory, compatibility documentation and relevant tests, rerun the review/audit pipeline, then bump the release version on main. Development-only rows stay disabled until their own behavior and dependencies are verified.
