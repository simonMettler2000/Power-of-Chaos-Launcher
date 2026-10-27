# HD card preview component

This directory contains authored source and reproducible build tooling only. The
native matcher copies the exact 800x600 RGB565 upload presented by the D3D9
provider, verifies the whole visible 200x290 card raster, and substitutes the
corresponding local 800x1160 master in the higher-resolution output. Static large
previews cover Yugi, Kaiba and Joey. Unknown pixels, ambiguity, occlusion, fades,
unsupported layouts or render conditions retain the base display. Small or
rotated cards are outside this first version. RGB555 is supported by the CPU
matcher/pack format; the launcher profile requires RGB565 because the provider
reports RGB565 masks.

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
production output. The final ddraw.dll must be 502784 bytes with SHA256
4156f862bd6363b9a3925af75ac924e732751f035d84517c88be8d02d604a925. The
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
adapter, with no execution. Its output must also be a fresh repository .local
path. Run the authored CPU test explicitly from that output directory if desired.
The bounded verify-tools.ps1 -ScratchDirectory <fresh-ignored-path> checks rejected
output paths, missing/corrupt inputs and script syntax without starting a compiler.
The optional --synthetic source patch and Build-Provider.ps1 -Synthetic produce a
separately pinned observer build for an authored SyntheticProviderProbe host.
Never use original game executables or the old consumed GDI probe as automated
tests. These hosts provide synthetic evidence only.

Actual installed-game display qualification and manual validation remain pending:
verify the exact runtime preview raster for every intended game/card, its visible
HD detail, fallback during transitions/occlusion and long-running device/window
lifecycle behavior. Authored host success alone does not establish in-game HD.
