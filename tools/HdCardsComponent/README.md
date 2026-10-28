# HD card preview component

This directory contains authored source and reproducible build tooling only. The
native matcher takes an owned copy of the complete 800x600 RGB565 frame, verifies
an entire visible 200x290 preview against a local reference, and substitutes the
corresponding full 800x1160 master in the larger output. HD mode defaults to GDI
for all three games, with an 800x600 logical frame and a 1280x960 resizable 4:3
window. Timing settings are unchanged. The separately selected Original aspect
ratio mode keeps its existing renderer behavior. The D3D9 HD backend remains
compiled and covered by synthetic adapter/provider tests; those tests do not
establish installed-game presentation on every display device.

GDI scales the complete master directly to the physical preview with HALFTONE,
so fine artwork and text strokes contribute when the image is reduced. It sets
the brush origin after selecting HALFTONE, as required by the
[Windows API contract](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setstretchbltmode).
The caller's filter, brush origin and clip are restored on completion or failure.
If filter or brush setup fails, the complete original base remains displayed.
The base game's scaling and timing settings are unchanged. D3D9 already uses
linear minification and magnification for the artwork overlay.

The explicit preview regions are listed below; coordinates are the top-left
pixel in the 800x600 logical frame. Every region is exactly 200x290.

| Game | Duel preview | Card List preview | Deck Construction preview |
|---|---|---|---|
| Yugi | (5,72) | (11,88) | (7,89) |
| Kaiba | (5,72) | (16,86) | (7,89) |
| Joey | (2,74) | (6,87) | (7,89) |

V1 packs admit only complete, unoccluded exact matches at these nine positions.
The optional GDI V2 pack can additionally explain a preview as the complete
reference plus exactly one verified normal software cursor00/01. Recognition
uses the same owned frame, scans at most two colorful opaque row anchors,
verifies every opaque sprite pixel, caps placements at16, then compares every
116000 ROI byte against the full reference-plus-cursor composition. Candidate
overflow or ambiguous state/origin/card/region retains the base. No mouse API,
private game address, stale identity or ignored pointer rectangle is used.
The first cursor contract requires its complete64x64 sprite inside800x600;
unsupported clipped cursors, action sprites and unexplained layers fall back.

Cursor banks are local16KiB BGRA payloads with SHA256 in the V2 config. Native
conversion truncates to RGB565 and alpha4 (`q = A >> 4`). Admission uses separate
per-channel `floor(source*q/15) + floor(base*(15-q)/15)` terms. The foreground is
then composed above the HALFTONE master in a bounded owned physical32-bit DIB:
source channels are quantized to565 and expanded, and the same separate q/15
terms apply to each physical8-bit destination channel. An owned coordinate-field
DIB samples the complete global viewport using the inherited GDI base mode1/2/3;
no ROI-local phase assumption or game/windowDC readback is used. Foreground is
clipped to the HD ROI, so the outside base cursor is never blended twice. Each
owned canvas is limited to4 million pixels; failures restore the same snapshot.
Caller DC state and every temporary GDI object are restored/released. Unsupported
base modes retain the original display. This cursor revision requires fresh
independent review and actual all-three-game hover qualification.

V2 grammar adds a third line after `POC_HD_VISIBLE_V2` and `game TAB 1`:
`CURSORS_BGRA64_V1 TAB cursor00-absolute-path TAB sha256 TAB cursor01-absolute-path TAB sha256`.
The remaining reference rows are unchanged. Both banks and hashes are mandatory;
invalid/missing banks clear the old native context. Core validates their exact
size/hash and includes them in input ownership; config content changes produce a
new cache identity. V1 remains unchanged. The D3D9 backend accepts V1 only and
fails closed for V2; cursor-aware HD is currently a GDI feature.

Unknown or ambiguous pixels, fades, transitions, unsupported formats/layouts and
render conditions retain the original base image. Small cards, rotated hand
cards, field cards, deck backs and graveyard cards are outside this version.
RGB555 is covered by the CPU matcher/pack format; the launcher HD profile
requires RGB565 because the provider reports RGB565 masks.

A master must be a complete 800x1160 RGB8 non-interlaced PNG (color type 2,
compression/filter methods 0), with no metadata or ancillary chunks: one IHDR,
consecutive nonempty IDAT chunks and one final IEND only. The native admission
path bounds the complete file to 16 MiB and checks chunk CRCs, the complete zlib
stream, Adler checksum, row filters and decoded image before either GDI or
D3D9/WIC rendering. A separate 32 MiB cap applies to live decoder allocation
payload. That decoder budget excludes allocator headers, caller-owned PNG/IDAT
buffers, WIC allocations and other process memory; it is not an RSS limit.
A file that fails admission retains the base display.

The GDI path converts the shared strict decoder's validated RGB directly into
owned opaque BGRA pixels; it does not initialize COM or invoke WIC. D3D9 retains
its existing strict-admission-plus-WIC path. The BGRA suite compares every pixel
against arithmetic fixtures and the previous validated WIC path, including row
filters and Adam7, and checks every decoder-allocation failure with recovery.
This simplification has no established connection to intermittent black rasters;
new actual qualification and independent integration reviews remain required.
Earlier actual-frame evidence retains its recorded provider fingerprint; it is
not transferred to this decoder revision, even though geometry/filter are unchanged.

Setup requires Windows, PowerShell 7, Python 3.9+, Visual Studio 2022 toolset
14.41.34120 and Windows SDK 10.0.22621.0 at the pinned installed paths. All compiler,
header, library and VC targets hashes must match toolchain-pins.json. The upstream
source archive, proxy archive and notice inputs are the six exact offline inputs
listed in component-pins.json and tools/DisplayComponent/component-pins.json.
They can be acquired using the existing DisplayComponent instructions and their
pinned HTTPS URLs. Nothing is downloaded by this setup. No third-party source,
DLL, notice payload, reference raster or artwork is included here.

From the repository root, create a fresh ignored staging parent, then run:

```powershell
New-Item -ItemType Directory .local/hd-build/Components
& tools/HdCardsComponent/setup-component.ps1 `
  -WorkDirectory .local/hd-build/work `
  -Destination .local/hd-build/Components/hd-card-artwork-v1 `
  -InputCache <directory-containing-the-six-pinned-inputs>
```

Output paths must be fresh, disjoint, link-free and inside an enclosing Git checkout's
ignored .local directory. Setup reconstructs the pinned Original Aspect Ratio
source profile using the existing authored recipe, applies the authored HD patch,
and compiles a new x86 provider. It refuses any different complete source tree or
production output. The final ddraw.dll must be 546816 bytes with SHA256
70672798af371ce85c0264a52fd83f83d6e0977d32713299c94f9739245f5430. The
bundle includes the independently pinned winmm.dll and all eight notices copied
from the verified aspect bundle. A complete bundle is published by directory
rename. Build logs and receipts remain beside it. Use a short checkout/scratch path on
Windows; deeply nested source paths can exceed compiler path limits. Setup never loads any component
or executes an original game. Copy the verified hd-card-artwork-v1 directory into
the local launcher's Components directory when preparing a manual runtime test.
Keep DLLs, third-party source/notices and all generated output outside Git.

The pack tool consumes a local accepted-delivery manifest and local original card
PNG references. It preserves original card/art identities in pack-receipt.json;
the native TSV contains one key, absolute raw-reference path and absolute master
path per row. Raw little-endian RGB565/RGB555 references are 116000 bytes. It does
not infer card IDs from resource indices, fabricate missing padding for 199-wide
inputs, download art or edit assets. Use --format 565 for the launcher profile:

```powershell
python -B tools/hd_cards_build_visible_pack.py --delivery <local-manifest.json> `
  --source-root <local-original-png-root> --output .local/visible-pack --format 565
python -B -m unittest discover -s tests -p test_hd_cards_visible_pack.py -v
```

Build-Native.ps1 builds only the authored CPU/graphics/configuration test hosts and
adapter, with no execution. It requires -ExternalSourceDirectory pointing to the
complete pinned external aspect-provider source tree, including the pinned
LodePNG C/H inputs; vendor source is never copied into this authored repository.
Its output must also be a fresh repository .local path. The strict verify-native.py
--external-source <pinned-aspect-source> --output <fresh-ignored-path> recipe builds
and runs nine authored CPU/memory-DC suites without loading a provider or opening
a window.
The bounded verify-tools.ps1 -ScratchDirectory <fresh-ignored-path> checks rejected
output paths, missing/corrupt inputs and script syntax without starting a compiler.
The optional --synthetic source patch and Build-Provider.ps1 -Synthetic produce a
separately pinned observer build for an authored SyntheticProviderProbe host.
The GDI probe reads the observer's actual persistent window HDC after the full
base and overlay; a separate unhooked real-window process draws the independent
expected frame on the same display device before observation. Every output pixel
is compared. Never use original game executables as automated tests. These hosts
provide synthetic evidence only.

Earlier pixel QA covered three supplied large-preview frames with the delivered
serif masters and prior nearest-pixel filter: Yugi's De-Spell in Deck Construction,
Kaiba's Stim-Pack in Deck Construction and Joey's Graceful Dice in Card List.
Those earlier observations do not qualify the changed smooth-filter output.
Fresh actual-display pixel QA, maximized/fullscreen behavior, uncaptured
layouts/cards, transitions/occlusion and long-running window/device behavior
remain pending. Headless tests compare every RGB pixel to an independent GDI
HALFTONE expectation for generated high-contrast thin strokes at native,
default and fractional preview sizes, prove the result differs from nearest
selection, and check filter/brush failure fallback and caller-state restoration.
The separate real-window oracle declares HALFTONE before provider observation.
The authored synthetic matrix compares every RGB pixel on the actual display
device across all nine preview positions, generated thin-stroke masters and
guard/lifecycle cases. These generated inputs do not qualify installed-game
frames or the separately supplied artwork pack.
Full release qualification, independent integration reviews and the exact-commit
audit remain pending; synthetic host success alone does not establish those claims.

Cursor-aware presentation requires a viewport at least800x600. At smaller sizes,
it displays the same owned original base, including its cursor; GDI modes1/2 can
combine or delete reduction samples, so a single foreground texel is unproved.
V1 and unoccluded exact-card minification keep their existing behavior.

Opaque cursor foreground uses the RGB565-to-RGB8 bit replication verified
against owned GDI conversion of all65536 words, preserving native base colors
at preview crossings. Partial alpha still uses separately floored q/15 terms
against the new physical HD background; it is not a retained old-card pixel.
