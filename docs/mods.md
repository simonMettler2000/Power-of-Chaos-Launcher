# Mods

The checkbox at the left of each row controls that mod for all three games. A title and description follow it.

## Skip Splash Screens

Tick the switch before pressing Play. The mod skips Konami and the other publisher logos in the normal startup sequence and continues into the normal title/menu entrance animation. It retains the title music, menu options and input setup. It does not skip later game-requested video sequences.

Unticking the switch uses the original executable on the next launch. There is no continuing background hook to unload once the startup sequence has finished. An already-running game is left alone. The setting is saved between launcher sessions, independently of which games are currently installed.

### Supported inputs

The following SHA256 values identify the exact inspected executable revisions. Filenames and discovery headers alone do not establish support for a mod.

| Game | Executable | Original SHA256 |
|---|---|---|
| Yugi the Destiny | `yugi_pc.exe` | `1e5d81b1e44edf09e91addab582ef963694aa285e4be5eb5ad8e0ce4ab04664c` |
| Kaiba the Revenge | `kaiba_pc.exe` | `a829b16eee11356159850029f5a809886b47c33498ce1d849dd1591a603c0309` |
| Joey the Passion | `joey_pc.exe` | `08018ebbc73f3fa51fdf220e903f69cd9a22572e508ea9cd145853abf8ed2dda` |

The inspected Joey input has a stale PE SizeOfCode field; its retail provenance has not been established. Support here identifies those exact bytes, not a claim that this is a canonical retail release. Alternate executables and other regional or modified revisions require separate inspection. A refusal affects only the modded launch: untick the switch to use the ordinary launch path.

### Local preparation

For a supported input, the launcher changes the initial logo timer and index in a private copy. The first logo update follows the existing completion path before drawing a logo, and the ordinary scene transition initializes the title menu. It does not redirect a scene factory, inject code, replace assets, alter saves or change copy-protection logic.

The executable copy is kept under the launcher's local application-data directory. Game archives remain in their installation, which is used as the working directory. Preparation verifies the original and resulting hashes and refuses an unexpected version or damaged cache. No game executable or binary patch is distributed by this repository.

Binary-only mod copies use a compact `mod-cache/v3/<game>/<SHA256>` directory. The identity includes the game, selected mods and full original/result hashes; every launch still verifies both executable contents. This avoids Windows process-launch failures caused by the previous cache's long nested paths. Old cache directories are left alone. Excessively long custom launch paths are rejected with an explanation before process creation. Launcher messages are selectable with Ctrl+C and have a Copy button for the complete diagnostic.

### Verification limits

Development checks inspect installed inputs as data, verify patch results in memory, and exercise the launcher with synthetic fixtures and fake process runners. UI tests operate the actual Avalonia controls without pressing Play. Neither original nor modified games are executed during development or automated testing.

Actual logo-free startup, audio/rendering, module-location assumptions, operating-system permissions and Wine behavior therefore remain manual compatibility checks. A passing launcher build or exact patch hash cannot substitute for running your own installation. The native title fade is deliberately retained and may still take a short time before menu options appear.

## Skip character interactions

This global switch skips the initial character greeting and the character interruptions during duels: spoken reactions, portrait/special-monster reaction cut-ins and the pauses associated with those presentations. It keeps ordinary duel music, card sound effects and normal card animations, including fusion visuals and their normal music behavior. The end-of-duel win/loss/draw presentation is retained.

The implementation bypasses optional character event callbacks and uses native skip/continuation branches before creating queued presentations. Standalone voice requests and fusion voice cues receive separate treatment where needed. These are distinct paths in each game; the profiles are individually derived for the three exact executable hashes listed above. No game code, sound file or modified executable is bundled. Existing code is selected by factual pointer/relative-target metadata; no instruction payload is inserted.

Skipping the optional character callbacks also removes random choices for their cosmetic variants. Rules, decks, rewards and saved collections are not patched, but the exact later random sequence from an identical seed is not guaranteed to remain the same.

| Splash screens | Character interactions | Next launch |
|---|---|---|
| Off | Off | Original executable |
| On | Off | Skip publisher logos; normal character presentations |
| Off | On | Normal publisher logos; skip greeting and in-duel character interruptions |
| On | On | Both modifications |

Each enabled combination has its own full-file verification and isolated cache identity. Turning off one mod keeps the other selected. Turning both off returns to the unchanged original executable when Original aspect ratio is also off. Changes take effect on the next launch and do not patch a running game.

Automated checks exercise the four combinations with synthetic fixtures and fake processes, and verify the real pinned outputs entirely in memory. They never execute original or modified games. Actual duel behavior, audio/rendering and Windows/Wine compatibility remain manual checks under the project's no-execution rule.

## Speedy mode

Tick **Speedy mode** in Mods to pass the built-in `-speedy` option when you next start a game. It applies to Yugi the Destiny, Kaiba the Revenge and Joey the Passion. The option selects the game's faster timing mode, which raises its usual 30 FPS cap to 60. The checked Yugi executable is Version 2.

The setting requires no additional files. It works with both Original aspect ratio and HD card artwork because it passes the same launch argument to the executable selected by those mods. Untick it to launch at the game's default speed.

## Original aspect ratio

This global switch uses the same supported executable hashes above. All three inspected targets request an 800 x 600 picture. It starts windowed at that size, constrains Windows drag resizing to 4:3, and centers the scaled picture with black bars when the window fills the work area or the monitor. The maximum button uses a custom workarea fill/restore operation; it does not set the Windows native maximized state bit. Alt+Enter toggles borderless fullscreen.

Original aspect ratio applies on the next launch. With only Original aspect ratio enabled, the private executable has exactly the original bytes. With either of the startup mods enabled, its bytes match that combination's verified output. Display DLLs and a fresh global-only configuration live beside that private executable. The games still read their archives from the installation directory.

The wrapper leaves game timing to the game, so Speedy mode also works with this display option. Animation can stop during interactive drag resizing, and native catch-up after a short delay is preserved.

Turning this mod off selects the original or binary-only cache path. The launcher refuses an aspect-off executable directory containing either display proxy DLL, so a stale display cache cannot silently keep the component active. Unsupported executable versions, missing components and invalid caches fail before starting a process.

See [display setup](display.md) for component provenance, local prerequisites, tests and platform limits. A passing synthetic host proves only the exercised DirectDraw calls; it does not prove every game scene, physical device, graphics driver or Wine runner.

## HD card artwork

This global switch draws cards from high-resolution pictures in the card list, deck construction and duels: large previews, hand cards, field cards, graveyard and card backs, at every size the game uses and also while a card moves: dealt from the deck, flying to the field, flipping, fading, glowing, dimmed, breaking apart when it is destroyed, or under a translucent effect such as the shine on a newly summoned monster. It uses the same supported executable hashes as the other mods. Tick it before pressing Play; it applies on the next launch.

The pictures are a separate download. Get `Power-of-Chaos-HD-Card-Artwork-v3.zip` from the Releases page, extract it completely, then use **Choose HD image folder** in the HD card artwork row and select the extracted folder or its `images` folder. Keep the file names. The folder stays where it is; the launcher only records its location. The switch cannot be ticked before a folder with pictures is chosen.

Pack v3 has the same 773 file names as v1 and v2. Every card picture in it is repaired: the dark square around the icon and the right end of the name bar on all cards, on monsters also the band behind the level stars and the ground of the ATK/DEF line, the right border of the text box where it was smeared, and the copyright line. Artwork, names, card text and values are unchanged, and the card back is the same file. (v2 had repaired the 264 Normal monsters only.) A v1 or v2 folder keeps working, and v3 can be extracted over it.

The game plays in a borderless 4:3 picture at desktop size with black bars as needed. Alt+Enter switches to a resizable 4:3 window and back. HD card artwork brings its own build of the display component and leaves timing to the game. When both display switches are ticked, the HD component is used. It combines with Speedy mode, Skip Splash Screens and Skip character interactions.

Only the card itself is replaced. Whatever the game draws opaquely over a card (cursor, text, counters, damage numbers, dialog panels, opaque effect sprites) stays exactly as the game drew it. A highlighted, dimmed or half-faded card keeps that look on its HD picture, and a translucent effect over a card is drawn over the HD picture the way the game mixed it. Cards without an HD picture, and any single draw the component cannot identify or explain with certainty, keep the game's own picture rather than a guess.

The component needs Direct3D 11. Without it the game still runs with its normal picture. The component bundle `Components/hd-card-artwork-v2` must be beside the launcher; the row says so when it is missing. See [the component notes](../tools/HdCards/README.md) for how it works and how to build it, and [HD card artwork status](hd-renderer-status.md) for what has been verified.

## New card rewards

This global switch decides which cards the games give you for winning, and how many copies of them. The number of different cards is the games' own and stays as it is: one card for a won duel, three for a won match. With the switch on, every one of those cards is a card you do not own yet, and you get it three times at once, the most a deck can hold. A match gives three different new cards, three copies of each. Tick it before pressing Play; it applies on the next launch and uses the same supported executable hashes as the other mods.

Without the mod the games draw each reward card evenly from their whole pool, owned or not, and add one copy. They only draw again, at most twice, once you own four fifths of the pool, and only when no card of the reward was new. With nine tenths of a pool owned, a single-duel reward is new about one time in four.

- Cards you do not own come first. Once none is missing, rewards go to the cards you hold once or twice and bring them to three copies.
- When a reward has more cards than there are cards of that kind left, the next kind fills it: missing cards, then cards held once or twice, then any other card.
- When you hold three copies of every card a game can give, rewards are what they are without the mod: any card, one more copy.
- Each game gives cards from its own reward pool only, with or without the mod: 155 cards in Yugi the Destiny, 315 in Kaiba the Revenge and 350 in Joey the Passion on the inspected installations. Copies are counted in the shared collection save.
- While Temporary all cards is on, every card is held three times, so this mod has nothing to give; the Mods row says so.

The game's own reward screen shows each card once; the three copies are in your collection when you look at the card list or build a deck. The game counts and saves the cards itself. The mod writes no save and changes no installed file. It combines with every other mod.

The mod works through the gameplay component, a small library written for this launcher. The launcher places it, with a one-line settings file, beside its private copy of the executable; with Original aspect ratio or HD card artwork it goes into their display cache. A launch without the mod never has the component beside it. The bundle `Components/gameplay-v1` must be beside the launcher; the row says so when it is missing. See [the component notes](../tools/GameplayComponent/README.md) for how it works, how it was verified and what was not covered.

## Custom opponent decks

This global switch lets the opponent play decks you made. Press **Open opponent decks** in its row to see and build decks; the switch decides whether your ticked decks are used. It applies on the next launch and uses the same supported executable hashes as the other mods.

**Looking.** For each added game the window lists the decks that game brings for its opponent (8 in Yugi the Destiny, 19 in Kaiba the Revenge, 58 in Joey the Passion on the inspected installations) under the names of their files. Selecting one shows its main, Fusion and side deck: the game's own small card picture, the card's name in the launcher language, its kind, level, ATK and DEF, and how many copies the deck holds. The **i** beside a card, or selecting it, shows the card's text. Everything is read from your installation when the window opens; nothing of it is stored by the launcher.

**Building.** The game's own decks are read-only. **Copy to my decks** makes a copy you can change; **New deck** starts empty. Add cards from the list at the right, which can be searched by name and narrowed to monsters, Spell cards, Trap cards or Fusion monsters; **+** and **−** beside a card of the deck add or remove one copy. **Save deck** asks for a name the first time. Your decks are kept in `opponent-decks.json` in the launcher's data folder.

**Rules.** They are the sizes the games' own deck storage has, and the launcher holds every deck to them before it can be saved and again before each launch:

- 40 to 80 cards in the main deck, up to 15 in the Fusion deck, up to 15 in the side deck;
- at most three copies of a card, counted over all three parts;
- only cards that game supports (Yugi the Destiny 155, Kaiba the Revenge 466, Joey the Passion 771);
- Fusion monsters in the Fusion deck, and nothing else there.

**Choosing.** Tick the decks the opponent may use. A deck you just saved is ticked. With several ticked for a game, each duel takes one of them at random; in a match the opponent keeps one deck for all its duels. With none ticked for a game, or with the switch off, that game uses its own decks and its own way of choosing among them. The Mods row says how many of your decks are in use for each game.

**What the opponent does with a deck.** The games' opponent was only ever written for its own decks. Cards that are in none of them are marked "Not in any of the game's own decks" in the list. In the runs that were made the opponent summoned and attacked with such monsters in all three games, but it chooses by attack strength and will, for example, summon a monster whose own effect then destroys it. How it handles every Spell and Trap card it never had is not known; a deck of such cards may play weaker than you expect.

The tutorial and the title demo do not use these decks. Installed files are not changed: at launch the ticked decks are written as deck files beside the gameplay component in the launcher's private copy, and the component hands them to the game when it asks for an opponent deck. With Original aspect ratio or HD card artwork they go into the display cache. The bundle `Components/gameplay-v1` must be beside the launcher; the row says so when it is missing. See [the component notes](../tools/GameplayComponent/README.md).
