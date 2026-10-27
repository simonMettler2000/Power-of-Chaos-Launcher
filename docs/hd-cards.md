# HD Cards: implementation in progress

This branch implements the local card pack and renderer foundation for **800 × 1160**, **20:29**, lossless **8-bit RGB PNG** masters. It targets the three games through explicit game/card/art/side identities. Artwork stays on the user's computer and is not included or downloaded.

**The game mod is not usable yet.** The HD entry in Mods remains disabled. This code does not replace installed archives, hook a game or start a game. A finished 800 × 600 frame enlarged afterward cannot show the extra detail. The remaining engine adapter must preserve asset identity and draw into a larger physical target before presentation.

## Implemented source

- `PowerOfChaos.HdCards`: strict versioned manifest; explicit front/back and art variants; bounded PNG verification/decoding; read-only, lazy LRU cache. Missing or invalid entries return false so the adapter can preserve original rendering.
- `PowerOfChaos.HdRendering`: separates the unchanged 800 × 600 logical viewport and card footprint from physical source/output pixels; supports explicit contain/cover, clipping and quarter-turn rotations. RGB data is sampled directly from the HD master into the larger target, with no old small card cache in between.
- `hd_cards_import.py`: builds a new private pack manifest from a local reconstruction delivery, verifies rendition hashes/headers and reuses its images. It copies no artwork and refuses writes inside the delivery.
- `PowerOfChaos.HdCardProbe`: full local-pack decoding and a physical-raster proof. It is a developer probe, not a game mod or replacement for the engine adapter.

The cache limit covers resident decoded RGB images. Caller-retained images and temporary decode buffers are additional memory. Pack directories must not be concurrently renamed or changed by an untrusted writer. Changes to cached artwork require reopening the pack. Paths refuse links, traversal and device names; these checks are not race-free filesystem capabilities.

An exact `(game, cardId, artId, side)` is required. The loader does not guess by card name, substitute variants, reveal hidden cards or treat a selected resource as visible. A game's resource-list index must be independently mapped to the pack identity; it is not assumed equal to `cardId`.

## Local pack and proof

The importer accepts the `power-of-chaos-reconstructed-card-delivery-v1` format produced by the local artwork workflow. Output belongs outside the delivery and source repository. Replace the example paths with your local paths:

```sh
python -I -B tools/hd_cards_import.py --delivery DELIVERY/manifest.json --output PRIVATE/hd-pack.json
dotnet run --project tools/PowerOfChaos.HdCardProbe -c Release -- --manifest PRIVATE/hd-pack.json --assets-root DELIVERY/images --verify-all
dotnet run --project tools/PowerOfChaos.HdCardProbe -c Release -- --manifest PRIVATE/hd-pack.json --assets-root DELIVERY/images --game yugi --card-id CARD_ID --art-id ART_ID --proof PRIVATE/new-proof.png
```

The last command creates a new 3200 × 2400 RGB PNG containing one card at its original logical 200 × 290 size. Its physical 800 × 1160 rectangle is checked against every source pixel. This demonstrates preserved source detail in an authored renderer. It does not demonstrate game output, gameplay compatibility, all-angle animation, perspective, native effects or complete scene composition. PNG creation refuses to replace an existing output.

Use the [library schema](../src/PowerOfChaos.HdCards/README.md) for other packs. `AssetRootDirectory` is an explicit trusted caller selection; the untrusted manifest cannot select another root. Cards are opaque RGB; character/background remastering and alpha/masks are separate work.

## Checks and remaining work

```sh
python -I -B tools/verify_hd_cards.py --output .local/hd-checks-NEW
python -I -B tests/test_hd_cards_import.py
python -I -B tools/publication_guard.py --working-tree
```

The verifier builds fresh Release and Debug artifacts, runs the authored pack and physical-raster tests, and runs the existing launcher regression harness. It never executes a game. Tests create diagnostic images and synthetic metadata, not game data. Platform-specific skips are explicit; a Windows result does not establish Linux/macOS graphics or Wine behavior.

The existing launcher harness requires its normal project output layout and short Windows fixture paths for the legacy display component. The verifier preserves that layout. For a long checkout path, set `POC_LAUNCHER_TEST_ROOT` to a short private fixture directory before running it; it records that override in its receipt. This does not relax the display component's path limits.

Next is a reviewed engine adapter that proves authorized front/back selection, active visible presentation, object/resource lifetime and generation, exact resource/art mapping, clips and foreground ordering. Then it must feed the physical target through a compatible presentation path while retaining original simulation/input/audio timing. No ready flag, safe polling profile or hook is supplied by this branch. Cards in hands/field/deck editor/rewards, rotations and animations need separate coverage. Current aspect-ratio components and paused native 60 FPS work must not be overwritten or silently enabled.
