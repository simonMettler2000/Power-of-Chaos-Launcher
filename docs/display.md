# Original aspect ratio display component

The inspected games use an 800 x 600 image. The display mod starts at that size in a window, preserves 4:3 during Windows edge resizing, and centers the picture with black bars when filling a wider work area or monitor. Alt+Enter switches between windowed and borderless fullscreen. The maximize button fills/restores the work area using a custom operation; Windows' native maximized state bit is not set.

The mod does not change game timing fields or pass game speed/display arguments. The original limiter remains responsible for game updates. Dragging can pause presentation, and the original short-delay catch-up remains possible. There is no frame-rate selector in this mod.

## Build the optional component locally

Complete Windows Release downloads include this verified component and all required notices. End users do not run build/setup commands. An ordinary source build still needs either the pinned release inputs or a separately built component. The setup script never starts a game or a component DLL. To rebuild the native component explicitly, use PowerShell7 on Windows and the pinned Visual Studio2022 C++ toolchain: MSVC14.41.34120, Windows SDK10.0.22621.0. Compiler/linker hashes are enforced; a different toolchain needs a separately reviewed recipe and artifact pin rather than relaxing verification.

From the launcher repository, choose a new short build directory outside installed game folders. For example:

```powershell
$buildRoot = 'C:\PoCBuild'
New-Item -ItemType Directory -Path $buildRoot
pwsh -File tools/DisplayComponent/setup-component.ps1 `
  -WorkDirectory "$buildRoot\component-work" `
  -Destination "$buildRoot\original-aspect-ratio-v1" `
  -DownloadMissing
```

`-DownloadMissing` explicitly permits acquisition of the exact HTTPS inputs in `component-pins.json`. Without it, supply `-InputCache` pointing to those already downloaded, hash-matching files. Work and destination directories must be new; setup preserves existing output. `-VisualStudioRoot` can select another installation of the same pinned toolchain. The script validates archives and patch preimages, strips upstream build events, builds the local profile and refuses any output that differs from the expected DLL hash.

Copy the resulting **entire directory**, including all eight notices, to `Components/original-aspect-ratio-v1` beside the launcher executable. Close the launcher before updating its application directory. Never copy it into a game installation. In the launcher, enable **Original aspect ratio** before the next Play action. If setup is missing or any required file has changed, launch preparation explains the problem and starts no game. Untick the mod to use the ordinary display path.

The native cache must be short and contain only ASCII characters because the wrapper uses ANSI module-relative APIs. A suitable normal data path is retained. If a user's profile contains Unicode or would make the cache too long, Windows automatically uses a short current-user SID folder under ProgramData. That folder is created with a protected DACL for the current user, SYSTEM and Administrators; foreign owners, broadened permissions and links are refused. No manual environment setting or elevation is normally needed. If no secure location is available, the launcher gives an actionable error. Linked paths and cache/install/component-directory overlap are also refused. Game-derived executable copies remain private and must never be published; only the separately licensed display dependencies belong in the reviewed Windows download.

## Provenance and local modifications

The renderer is built from [cnc-ddraw v7.1.0.0](https://github.com/FunkyFr3sh/cnc-ddraw/tree/541b5de218ec3fbd6ea91e606ebfadc07c1786b0). The WinMM bootstrap is the exact universal proxy from that release. The games use COM to create DirectDraw, so the bootstrap loads the provider before entry. An unmodified upstream DLL is not interchangeable with this profile.

The authored recipe removes the upstream codec-registration/deletion, global screensaver and process-affinity changes. It replaces maximize with the workarea-fill/restore operation. Existing native sizing, viewport and cursor transforms remain upstream. Transient timer-resolution/thread execution-state calls remain; no persistent system setting is intentionally changed by this profile. The recipe and full output pins are inspectable in `tools/DisplayComponent`.

| Local component | Bytes | SHA256 |
|---|---:|---|
| ddraw.dll | 424448 | `0f5f094dd1ab1bc23cfdc1fe4c1ed51d91c1c9a5fafd063c9897fa018606be91` |
| winmm.dll | 98304 | `1de1ed07c548e262767ba06891284ae0ecc39f6cc0217dd715594e5bc30e0e8a` |

The setup retains applicable notices for cnc-ddraw, petool, Microsoft Detours, LodePNG, MJP, Hyllian/Jararaca, Hyllian xBR and Khronos headers. Their exact source locations, transformations and hashes are metadata in `component-pins.json`. All notices are verified and accompany each private runtime copy. No upstream source, license text or DLL is stored in this repository.

## Isolation and platform limits

Each selected executable is verified against its supported whole-file hash. The display cache contains a locally derived executable, DLLs, notices, a minimal module-relative configuration and manifest. Game assets are read from the installation working directory. The display configuration starts windowed every launch, disables wrapper game-logic limiting and does not persist display changes. Inherited wrapper configuration overrides are removed only from the child environment.

Under Wine the child gets native-only WinMM/DirectDraw overrides, preserving unrelated user entries. No `winecfg` or persistent override is applied. Actual Wine behavior is untested. Wine can resize the outer window without the Windows sizing messages, so strict outer-window 4:3 while dragging is not established there. The viewport implementation can still fit an arbitrary client area with bars. Graphics-driver behavior, Wine prefix/runner support, physical input and every game scene require their own runtime checks.

## Verification

Core tests exercise all 24 combinations of game and three Boolean settings using authored inert fixtures/fake processes. Optional installed-input checks verify corresponding original/modified whole-file hashes entirely in memory. No original, copied or modified game executes in development tests.

`tools/DisplayComponent/verify-tools.ps1` checks local setup refusals and two fresh identical builds. `tools/DisplayProbe` provides a generated-pattern DirectDraw host and a separate observer, with no game content. Its normal and virtual-fullscreen modes exercise the COM/bootstrap/surface shapes found in the games. The observer must remain outside the proxy DLL directory. See the probe README for commands and its explicit limits.

Software/GDI diagnostics verified startup, resize, minimum size, workarea maximize, borderless bars and restore in both synthetic modes. Approved live Windows captures also verified automatic-renderer output in both modes, with logged physical mouse queries matching scaled image positions and black-bar clamping. Alt+Enter entered and left borderless mode correctly. Immediate captures during a transition sometimes showed the previous frame; settled captures showed the complete fitted image. Earlier white GDI captures of the GPU surface were not representative of its visible output on this host.

The resize checks use the component's own sizing-message seam; a physical edge-drag was not established through the bounded computer-use surface. Every edge, monitor/DPI configuration and graphics driver has not been exercised. Actual gameplay, game speed and Wine remain untested under the project's no-game-execution rule.
