# Synthetic display component probe

This is original test code for the launcher's display component. It generates an
800x600 quadrant/grid image and exercises DirectDraw through imported COM and
WinMM, using Microsoft Windows SDK interface definitions. It contains no game
code or assets and must never be used to execute a game during development.

The component must already be acquired and built with the separate
`tools/DisplayComponent` tooling. This tool never downloads dependencies or
changes the production renderer. The runner requires the exact patched DLL and
proxy hashes; the unmodified upstream DLL is rejected.

The native runtime directory must be short and ASCII: the pinned wrapper uses
ANSI APIs for module-relative INI lookup. The runner refuses paths that could
make it miss the precreated windowed profile.

## Build

Requirements: Windows, Git, Visual Studio C++ x86 tools and Windows SDK. The
defaults match the verification toolchain: MSVC 14.41.34120 and SDK 10.0.22621.0.
Explicit toolset/SDK/installation arguments allow another developer installation;
the private build receipt records the actual identities. A different toolchain
produces new evidence and does not inherit prior results.

Use a fresh ignored output directory, for example from the repository root:

```powershell
.\tools\DisplayProbe\Build-DisplayProbe.ps1 -OutputDirectory .\.local\display-probe-build
```

No Visual Studio shell activation is needed. The script invokes the selected
compiler/linker directly with explicit SDK include/library paths, `/W4 /WX`, and
reproducible build flags. It clears inherited compiler override variables only
in those children. Build logs, binaries, objects and receipts remain local.

## Run an automated component check

Supply the staged local component directory containing pinned `ddraw.dll` and
`winmm.dll`. Give every run a new ignored runtime directory:

```powershell
.\tools\DisplayProbe\Run-DisplayProbe.ps1 -BuildDirectory .\.local\display-probe-build -ComponentDirectory .\Components\original-aspect-ratio-v1 -RuntimeDirectory .\.local\probe-normal -Mode normal
.\tools\DisplayProbe\Run-DisplayProbe.ps1 -BuildDirectory .\.local\display-probe-build -ComponentDirectory .\Components\original-aspect-ratio-v1 -RuntimeDirectory .\.local\probe-virtual -Mode virtual
```

The runner verifies its own source/binary receipt and exact component pins. It
copies only the authored host and pinned DLLs to a fresh component directory,
precreates one authored INI, keeps the working directory separate and empty, and
clears the two wrapper config-override environment variables only in the child.
It does not alter installed games, registry settings, system display modes or
global input. The wrapper's normal cursor capture behavior may occur when the
test window is active. The independent observer stays in the build directory,
without a proxy or DirectDraw DLL beside it, and validates the host PID/class.

The native-windowed path uses `DDSCL_NORMAL` and primary `Blt`; the virtual path
uses `0x11` and a virtual 800x600x16 display mode followed by `BltFast`. Before
cooperative/display-mode calls, an SDK-defined interface check requires actual
method implementations to belong to the explicit pinned wrapper module. A
missing COM interception refuses the run rather than entering system-exclusive
DirectDraw. The profile keeps the wrapper windowed; its fullscreen toggle is
borderless and never requests a hardware mode change.

Six stages cover startup, resize, native minimum, workarea maximize, borderless,
and restore. The own-window sizing seam sends the component's sizing messages;
it is distinct from physical dragging. GDI diagnostic checks can be requested
explicitly with `-Renderer gdi`. GDI results cannot establish auto/D3D9 rendering.
Some GPU-backed windows return white through GDI capture APIs even when visible;
an auto capture failure needs a CUA window screenshot before drawing conclusions.

## Live visual and physical-input checks

Add `-Interactive` to either runner command and use a fresh runtime directory.
It returns the actual PID, window title and evidence directory immediately. The
owned window stays open for at most 180 seconds (`-InteractiveSeconds` can lower
the limit). An independent observer watchdog checks the exact authored host PID
and image path and terminates that child if initialization or presentation stalls
past the deadline. Its private `watchdog.log` records any forced termination;
that outcome cannot count as a pass. Press **N** to cycle the stages and **Esc** to close. Use only human
input or CUA on the identified real window. The program never calls SendInput or
SetCursorPos and does not install input hooks.

Left-button events log received coordinates, imported/hooked GetCursorPos,
ClientToScreen(0,0), their difference, stage and elapsed QPC time. That matches
the game's cursor-query pattern. Compare those logs with independent physical
click locations and actual screenshot bounds; a pure mapping calculation is
not a physical-input pass. Initial activation/cursor-lock clicks can be consumed
by the wrapper, so capture a subsequent click after the window is active.

The maximize implementation visually fills the workarea but does not set the
native WS_MAXIMIZE state bit. Drag-size minimums, Wine behavior, actual gameplay
and game speed need separate validation. This image workload does not prove a
game clock or establish compatibility. No arbitrary game FPS setting is used.

Keep runtime logs/screenshots/receipts private and ignored. Record exact source,
toolchain, component and executable hashes with any result. Independent reviews
and the launcher's publication/commit audit are still required before release.
