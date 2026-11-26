# HD card artwork component

This directory holds the authored source and build recipe of the display provider behind the **HD card artwork** mod. No upstream source, DLL, game data or artwork is stored here.

The provider is the pinned cnc-ddraw v7.1.0.0 source with the launcher's display profile (the same edits as `tools/DisplayComponent`) plus the `pochd_*` units in `src/`. It is loaded into the game by the unchanged WinMM bootstrap, exactly like the Original aspect ratio provider.

## How it works

The three games draw every picture with one image library. A card reaches the screen through a small set of that library's routines:

- the **plain copy** routine: a card at rest, sliding, or scaled upright, and the little blocks a destroyed card breaks into;
- three **mixing** routines (blend, shade, add): dimmed cards, glows, flashes, fades;
- three **quad** routines (plain, with a see-through colour, mixed in): a card that is turned, scaled or squeezed while it moves, such as a card dealt from the deck, a card flipping over, or a card flying to the field.

The source is always a card bitmap: the 200x290 preview picture, the 50x72 field picture, or a rotated or half-size copy the game derived from them. A moving card is drawn again on every frame at its next position, size or pose, so following these routines follows the card.

1. **Hook.** The provider wraps those routines, and two more that draw pictures with their own transparency (`pochd_game.c`). The original routine runs unchanged; the wrapper looks at the destination before and after the call. The executable is recognised by file name and by a hash of the first 48 bytes at each routine's address (FNV-1a, 64 bit); any other revision gets the plain picture. The source holds addresses and hashes only, no game code.
2. **Recognise.** A source image with card proportions is compared with the card bitmaps in the installation's own `data.dat` (`pochd_cards.c`). Each bitmap is reduced to a small colour grid; the closest grid wins only when it is close and clearly closer than the runner-up. This also yields the orientation, so the game's own rotated and half-size copies need no extra hooks. The archive's card list maps the bitmap name to the card number that names the HD picture file.
3. **Track.** A recognised draw leaves a layer on its destination: which card, where its four corners lie, and the pixels the draw wrote (`pochd_track.c`). Copies from a surface that carries layers, such as the duel side panel, carry them along.
   - A mixing draw is measured instead of assumed: the provider fits `after = card x gain + before x keep` over the drawn area and keeps the layer only when that fit explains the pixels.
   - A quad draw is traced back pixel by pixel into the card. It is believed only when what it left on the target is the card.
   - An effect drawn translucently over a card that is already there (the shine on a summoned monster, a flash, a ring, a translucent banner) does not end the layer. Each touched pixel remembers how much of the card still shows and what colour was added, so that `card x part + extra` is exactly the pixel the game made. Pictures with their own transparency are handled the same way, from their per-pixel opacity.
   - The games also darken a scene by keeping a second, dimmed copy of it on a hidden surface and mixing the two (the dark strip that opens behind the "duel start" banner). The dimming of the hidden copy is done by code the provider does not follow, so before a hidden surface's cards are used the provider compares them with the surface. If a whole card changed there by one colour mapping that keeps the order of its shades, the layer takes that in as part and extra. When the scene is then mixed with its dimmed copy, the same card lies at the same spot on both sides, and the pixel keeps both shares of it.
   - A card destroyed by an effect (Dark Hole and the like) breaks apart: the games copy it as some 470 blocks of 4x2 pixels and scatter them over half a second. Each block is a small unscaled copy from the card's bitmap. All blocks of one card in one frame share one layer that lists them, and each is drawn from its own part of the HD picture. Before version 0.3.1 every block took a layer of its own, which pushed the other cards on screen out of the frame's 255 layers for as long as the effect lasted.
   - A layer that was not drawn for the current frame is dropped once less than half of it is still on screen, so the trail of a moving card never lingers.
4. **Seal.** When the game presents its canvas, every layer claims only the pixels that still hold what the tracker expects. Whatever the game drew opaquely afterwards (cursor, text, counters, damage numbers, dialog panels, opaque effect sprites) changed those pixels, so it stays exactly as the game drew it. If the whole card changed by one consistent colour mapping (hover highlight, dimming, fade to a colour), the same mapping is applied to the HD picture. If only part of the card changed that way and the rest is untouched (the menu wipe of Kaiba the Revenge blacks out every other line first), the untouched pixels stay with the card as drawn and the changed ones get the mapping. Nothing is substituted on a guess.
5. **Present.** A Direct3D 11 presenter (`pochd_render.cpp`) draws the game's 800x600 picture into the provider's 4:3 viewport and the HD pictures on the claimed pixels, with the corners, gain and per-pixel part and extra the game used for that frame. Pictures are decoded on background threads and kept within a memory budget. Without Direct3D 11 or its shader compiler the provider's GDI presenter shows the plain picture.

Game timing, input, saves and installed files are untouched. The provider reads `data.dat` and the chosen picture folder; it writes one small `pochd.log` beside itself in the launcher's private cache.

## Supported executables

These are the same executable revisions the other mods support (see `docs/mods.md` for their hashes). Kaiba and Yugi carry the 2003 build of the library, Joey the 2004 build.

| Routine | Yugi the Destiny | Kaiba the Revenge | Joey the Passion |
|---|---|---|---|
| Copy | `0x0049C770` | `0x0049EB70` | `0x004ADC10` |
| Blend | `0x004C0730` | `0x004C2B30` | `0x004C84B0` |
| Shade | `0x004C0230` | `0x004C2630` | `0x004C77E0` |
| Add | `0x004F34C0` | `0x004F58C0` | `0x004F0810` |
| Picture with transparency | `0x004C19A0` | `0x004C3DA0` | `0x004CB6F0` |
| Picture with transparency, at a strength | `0x004C1CF0` | `0x004C40F0` | `0x004CBDD0` |
| Quad | `0x00517550` | `0x00519950` | `0x00520370` |
| Quad with see-through colour | `0x00516C70` | `0x00519070` | `0x0051EB80` |
| Quad mixed in | `0x0042C6D0` | `0x0042C6B0` | `0x004231E0` |

Interfaces, the same in all three games (the first two rows are methods of the target image):

- `Copy(source, x, y, sourceRect, targetSize, clip)`, also the picture with transparency
- `Mix(source, x, y, level, sourceRect, targetSize, clip)`, also the picture with transparency at a strength
- `Quad(source, sourcePoints, targetPoints, option, option, count)`
- `MixQuad(target, source, level, sourcePoints, targetPoints, option, option, count)` (not a method)

A picture with transparency holds four bytes a pixel: the RGB565 colour and a 0..15 opacity. The mixing strength counts in sixteen steps through a table; level 128 keeps 7/15 of what was underneath.

How the set was established: every drawing routine reachable from the library's dispatch tables was logged while the games ran, write breakpoints on card positions on the canvas named the code that put pixels there, and read breakpoints on card bitmaps named the code that read them. Everything found is one of the routines above, the game building its own derived card images, or image loading.

## Build

Requirements: Windows, PowerShell 7, Python 3.9+, Visual Studio 2022 or its Build Tools with the C++ x86 toolset, and the pinned upstream archive `source-v7.1.0.0.zip` (see `tools/DisplayComponent/component-pins.json` for its URL and hash). The script downloads nothing and starts no game.

```powershell
pwsh -File tools/HdCards/Build-HdComponent.ps1 `
  -SourceArchive C:\PoCBuild\inputs\source-v7.1.0.0.zip `
  -WorkDirectory C:\PoCBuild\hd-work `
  -AspectBundleDirectory C:\PoCBuild\original-aspect-ratio-v1 `
  -BundleDirectory C:\PoCBuild\hd-card-artwork-v2
```

`-AspectBundleDirectory` is a verified Original aspect ratio bundle; its `winmm.dll` and eight notices are copied unchanged after their pins are checked. Copy the resulting `hd-card-artwork-v2` directory to `Components/` beside the launcher executable.

Adding `-BundleArchive <new .zip path>` also writes the archive a release acquires: the ten bundle files, flat, in ordinal order with fixed timestamps. It is written only when the build matches the pinned provider. `tools/Release/release-settings.json` pins that archive; `docs/releases.md` describes how a Windows download picks it up.

The same sources and toolchain give the same `ddraw.dll`. `component-pins.json` records the toolchain and provider fingerprint. Update it and `HdCardArtworkComponents.Provider` together when the provider changes. For a GitHub release, publish the matching component archive and update its URL and fingerprint in `tools/Release/release-settings.json`.

## Provider settings

The launcher writes `pochd.ini` (UTF-8) beside the private executable:

| Key | Meaning | Default |
|---|---|---|
| `images` | Folder with the HD pictures (`NNNN - name.png`, `card-back-shared.png`) | none |
| `archive` | Game archive | `data.dat` in the working directory |
| `filter` | Base picture: 0 nearest, 1 linear, 2 sharp bilinear | 2 |
| `texture_megabytes` | Memory budget for HD pictures | 384 |
| `debug` | Diagnostics level, see below | 0 |

The environment variable `POCHD_DEBUG` sets the same level for one run without touching the settings file.

## Verification

Launcher checks (`tests/PowerOfChaos.Launcher.Tests/HdCardChecks.cs`) cover the launch preparation with inert fixtures. The provider itself was verified against the three supported games on Windows 11 by running the real games, at the repository owner's request. `docs/hd-renderer-status.md` lists what was and was not covered.

The provider carries its own diagnostics for this:

| `debug` | Adds |
|---|---|
| 1 | a verbose log and the command file |
| 2 | one log line for every mixed card, turned card and effect over a card; a `piece` line for each of the first forty small draws of cards (under about a hundred pixels) in a run, and for every frame that has them a `pieces` line with their number, written when the next such frame begins; a `loss` line for every card drawn for a frame of which more than a tenth is shown from no HD picture; a `recoloured` line for every card taken over from a hidden surface after it was dimmed there; a `stale` line for a recently drawn card on a hidden surface of which more than a tenth is no longer held when that surface is copied on |
| 3 | for the `loss` cards a small bitmap of what the game drew, with everything shown from HD tinted green; and the card-shaped pictures that were not recognised |
| 4 | every card copy and every dropped layer, and for a scene mixed with a copy of itself how many card pixels were found on both sides |

A `loss` or `stale` line is not an error in itself: the cursor, a damage number, a dialog panel or a character picture over a card takes pixels from it by design. The bitmaps make it quick to tell those from a card that is really shown in the game's own resolution. The `stale` lines exist because `loss` only counts pixels a layer still holds at presentation: a card that stopped being followed on a hidden surface would otherwise go unreported.

With `debug` on, the provider reads one-line commands from `pochd.cmd` beside itself: `shot <file.bmp>` saves the presented picture, `burst<N> <prefix>` the next N presented frames (up to 99), `frame <file.bmp>` the game's own 800x600 picture (a file name containing `owner` tints the substituted pixels), `layers` lists the recognised cards in the log, `hd 0|1` switches substitution, `tint 0|1` marks substituted pixels on screen.

## Known limits

- The set of routines was established on the scenes that were run (see the status document). A card drawn by some other routine in a scene nobody ran would show the game's own picture; the level 2 diagnostics would not report it, because they only know cards the tracker saw.
- Where the game draws something opaque over a card, those pixels are the game's. That includes opaque effect sprites such as the rays of a starburst; the card between the rays stays HD.
- A card on a hidden surface that the game changes there in some way other than one colour mapping for the whole card is not followed from that point; it shows the game's picture until it is drawn again. The `stale` diagnostics report it. In the scenes that were run this only happened where a character picture or a banner had been drawn over the card.
- Small pieces of a card are followed when the plain copy routine draws them unscaled, which is how the games break a destroyed card apart. Pieces under about a hundred pixels drawn by a mixing or quad routine stay as drawn; the `piece` diagnostics list them, and none was seen. A frame takes at most 12288 pieces, about 26 cards breaking apart at once.
- An effect over a card is reproduced exactly at the game's resolution; how strongly the HD detail shows through it is measured for the mixing routines and taken from the routine's design for turned effects and for a scene mixed with a copy of itself.
- At most 255 card layers are substituted in one frame; the newest win. The busiest screen seen, a full deck construction page, uses 54. During a wipe that blacks out part of each card a card can take two layers.
- Cards without an HD picture in the chosen folder keep the game's picture.
- Wine has not been tested.
