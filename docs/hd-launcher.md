# Local HD card previews

In the black sidebar, open **Settings > Preferences** and choose the generated local HD pack folder. It must contain `yugi-565.tsv`, `kaiba-565.tsv` and `joey-565.tsv`. The launcher includes no artwork or native binaries. A separately built and verified `Components/hd-card-artwork-v1` bundle is also required; it contains the pinned HD `ddraw.dll`, the existing bootstrap `winmm.dll` and all eight attribution notices. Missing or incorrect prerequisites leave the global **Mods > HD card previews** control unavailable, with an explanation. An existing saved selection can always be switched off.

The switch applies to all three games on their next launch. Play captures the checkbox and pack folder before any asynchronous preparation, so edits affect subsequent launches. Splash skipping, character interaction skipping and original aspect ratio remain independent preferences. HD supplies its own 4:3 display path when enabled, with an 800 x 600 logical game frame presented in a 1280 x 960 resizable window using GDI for all three games, RGB565 and the existing unlimited timing settings. The original aspect ratio component bundle and cache remain separate.

Only the chosen game's RGB565 configuration is validated for Play. Metadata is strict UTF-8, at most 1 MiB and 1024 unique positive keys. The header is `POC_HD_VISIBLE_V1`, followed by numeric game 1, 2 or 3 and format 1, separated by a tab. Each remaining row contains a key, an absolute 116000-byte reference path and an absolute 800 x 1160 RGB8 non-interlaced PNG path, separated by tabs. Relative, UNC, device, traversal, control-character and linked paths are refused. Drive-letter paths on mapped volumes are not independently rejected; packs are trusted local inputs. Masters must contain only IHDR, consecutive nonempty IDAT and IEND chunks. PNG headers are checked here; the native component independently checks the complete restricted PNG/zlib stream and decoded image, then retains the base display if it cannot render it.

The launcher validates the pack before preparing any executable. It copies the selected configuration into a private, identity-based HD cache, verifies the native components, configuration and executable, and passes that snapshot through the child's `POC_HD_CONFIG` environment entry. It removes inherited HD configuration from every launch, including when HD is disabled. It never changes the launcher's global environment. Installed executables, archives and saves remain read-only; the child keeps the installed asset directory as its working directory. Referenced local artwork remains in its original pack location and must remain available for the session.

This candidate recognizes complete, unoccluded preview rasters already visible in the final frame and smoothly scales their full local masters directly to the physical preview using GDI HALFTONE. Fine strokes contribute to the reduced image instead of being discarded by nearest-pixel selection. The nine admitted 200 x 290 positions are listed in the [component scope](../tools/HdCardsComponent/README.md). Other scenes, small cards, rotated hand cards, field cards, deck backs, graveyard cards, fades, transitions and unsupported layouts retain the original image. Hidden cards are never identified from private game state. RGB555 and non-Windows hosts are not qualified by this launcher candidate.

Synthetic launcher tests build inert PE-shaped metadata and generated pack headers, and use fake processes. They establish preparation and launch-plan behavior; they do not run original games or prove installed-game rendering. Earlier pixel QA of the prior nearest-pixel filter covered Yugi De-Spell in Deck Construction, Kaiba Stim-Pack in Deck Construction and Joey Graceful Dice in Card List with the delivered serif masters. Fresh actual-display validation is pending for this smooth-filter revision. Independent integration reviews, an exact-commit audit and the remaining actual validation are still required for release qualification.

Run the standalone checks with the pinned .NET SDK: `dotnet run --project tests/PowerOfChaos.HdLauncher.Tests -- <absolute ignored fixture folder>`. Build outputs and fixtures must remain in ignored storage.

The GDI path now obtains opaque BGRA directly from the shared strict PNG decoder; D3D9 retains its existing WIC path. This decoder revision has not inherited actual qualification from earlier provider fingerprints. No black-raster cause or fix is established by that simplification.


Optional normal software cursor previews (candidate)

The GDI cursor extension uses a local V2 pack. It preserves the exact whole-card
recognition rule by explaining every preview pixel as reference plus one verified
normal cursor, then placing that cursor above the HD master. Unknown cursor
states/layers and ambiguous matches retain the base. Other cards outside the
large preview remain outside this feature. Independent review and actual hover
checks are still required; previous screenshots retain their original DLL pins.

Use `python tools/hd_cards_add_cursor_bank.py --pack <existing-V1-pack> --output
<fresh-V2-folder> --archive yugi=<absolute-data.dat> --archive kaiba=<absolute-Data.dat>
--archive joey=<absolute-data.dat>` with explicit read-only original archive paths.
This writes the three RGB565 V2 configs and two normal64x64 banks per game into
a fresh local folder, leaving the old pack and archives untouched. Select that
folder in Settings only after qualifying the candidate component. Generated
banks contain original game pixels and must remain local, outside source control.
V1 packs still use the original unoccluded-preview path. D3D9 retains V1 support;
V2 cursor-aware presentation is GDI-only.

Cursor-aware presentation requires a viewport at least800x600. At smaller sizes,
it displays the same owned original base, including its cursor; GDI modes1/2 can
combine or delete reduction samples, so a single foreground texel is unproved.
V1 and unoccluded exact-card minification keep their existing behavior.

Opaque cursor foreground uses the RGB565-to-RGB8 bit replication verified
against owned GDI conversion of all65536 words, preserving native base colors
at preview crossings. Partial alpha still uses separately floored q/15 terms
against the new physical HD background; it is not a retained old-card pixel.
