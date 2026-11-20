# Gameplay component

This directory holds the authored source and build recipe of the component behind the **New card rewards** and **Custom opponent decks** mods. It is one small library, written for this launcher from scratch. No game code, game data or third-party source is stored here.

## How it gets into the game

The three games import seven functions of the Windows audio compression library, `msacm32.dll`. Windows looks for that library beside the executable first. For a launch with a gameplay mod, the launcher puts the component beside its private copy of the executable under that name, together with a settings file. The installation is not touched, and a launch without a gameplay mod never has the component beside it.

- **Standing in.** The component exports all 44 functions of the system library under the same names and ordinals and passes every call on to the real library in the system directory: same arguments, same registers, same return. The real library is loaded when the first call arrives.
- **Start-up.** When the game process loads the component, before any game code runs, it reads `pocgp.ini`, recognises the executable and switches the requested features on. It creates no thread, reads no game file and writes no save. Its only output is the small `pocgp.log` beside itself.
- **Recognising the game.** A game is accepted by its file name and by an FNV-1a (64 bit) hash over the code around each place the component touches. Any other revision is left exactly as it is, and the log says so.
- **What is changed.** For a requested feature the component changes where a few existing call instructions of the game lead: only the four-byte distance of a relative call is written, after checking that the call still leads where the supported revision's call leads. For a feature that was not requested nothing of the game is changed.

## New card rewards

The games give one card for a won duel and three for a won match. They draw each card evenly from the cards of their own reward pool, owned or not, and ask two things of their own code while doing so: how many cards the pool has, and whether a given card slot belongs to it. Each drawn card is then added once by one routine that only the reward code calls.

The component answers those two questions for the reward code only and stands in for the call that adds a card (`pocgp_rewards.c`). Everything else is the game's own and runs unchanged: the drawing, the rule that the cards of one reward differ, the "You get" screen and saving.

- While the player lacks cards of the pool, the pool the component reports holds just those cards.
- When no card is lacking but some are held once or twice, it holds just those.
- A reward that has more cards than that pool is filled with randomly chosen cards of the next kind (lacking, then held once or twice, then the rest), so that it can still consist of different cards.
- Every card of a reward is added by the game's own routine, called until the player holds three copies. The routine keeps the save's count of copies itself.
- When every card of the pool is held three times or more, the game's answers pass through untouched and a card is added once, exactly as without the mod.

The pool is whatever the game itself tests. On the inspected installations the reward code of Yugi the Destiny draws from 155 cards, Kaiba the Revenge from 315 and Joey the Passion from 350. Copies are the quantities of the shared save as the game holds them in memory.

Joey the Passion sometimes draws a reward a second time when the first held no new card. With new cards guaranteed this does not happen while cards are lacking; afterwards it happens as in the unmodded game, and the second draw is narrowed the same way. Joey adds the cards when the reward screen is left, the other two games before it is shown.

## Opponent deck files

For Custom opponent decks the component hands the game a deck file of the launcher's in place of a stock opponent deck (`pocgp_decks.c`, setting `opponent_deck`). The games read every file through one routine that tries the plain path before their archive, and their opponent decks are the `.ydc` files directly in one archive folder. The component stands in for that one open: when the game asks for such a file, it opens one of the configured deck files with the game's own open routine, so the game reads it exactly as it would read a loose file. Every other path is passed on untouched.

- With several deck files, one is taken at random each time a duel is set up. A file asked for again within three seconds gets the same answer, so one set-up never sees two decks.
- A deck file that cannot be opened falls back to the game's own deck for that set-up.
- The launcher writes the files beside the component (`opponent-deck-01.ydc` and so on) and has checked each deck against the game's own cards and deck sizes; the component does not look into them.
- Without the setting this part changes nothing in the game.

## Supported executables

The same executable revisions as the other mods (see `docs/mods.md` for their hashes).

| | Yugi the Destiny | Kaiba the Revenge | Joey the Passion |
|---|---|---|---|
| Reward code, recognised from | `0x00434CC0` (195 bytes) | `0x00435280` (195 bytes) | `0x00445920` (180 bytes) |
| Call: was it a match | `0x00434CE0` | `0x004352A0` | `0x0044592D` |
| Call: count the pool (stood in for) | `0x00434CF9` | `0x004352B9` | `0x00445947` |
| Call: pool bits of a slot (stood in for) | `0x00434D71` | `0x00435331` | `0x004459C4` |
| Call: this game's pool bit | `0x00434D7E` | `0x0043533E` | `0x004459CF` |
| Quantities owned | `0x00A1012C` | `0x00A1314C` | `0x00A53CCC` |
| Adding a card, recognised from | `0x00434E4A` (48 bytes) | `0x0043540A` (48 bytes) | `0x00445BAE` (48 bytes) |
| Call: add one copy of a slot (stood in for) | `0x00434E6A` | `0x0043542A` | `0x00445BCE` |
| File routine, recognised from | `0x0051A344` (48 bytes) | `0x0051C6F4` (48 bytes) | `0x00542A27` (48 bytes) |
| Call: open a plain path (stood in for) | `0x0051A364` | `0x0051C714` | `0x00542A47` |

Interfaces, the same in all three games: `int IsMatch()`, `void Count(int* owned, int* total)`, `unsigned short PoolBits(int slot)`, `unsigned short PoolBit()`, `void Add(unsigned short slot)`, all `cdecl`; the quantity of slot `n` is the byte at `quantities + 2 * n`; slots run from 1 to `0x45A`.

## Build

Requirements: Windows, PowerShell 7 and Visual Studio 2022 or its Build Tools with the C++ x86 toolset. The script downloads nothing and starts no game.

```powershell
pwsh -File tools/GameplayComponent/Build-GameplayComponent.ps1 `
  -WorkDirectory C:\PoCBuild\gameplay-work `
  -BundleDirectory C:\PoCBuild\gameplay-v1
```

Copy the resulting `gameplay-v1` directory to `Components/` beside the launcher executable. Adding `-BundleArchive <new .zip path>` also writes the archive a release would acquire; it is written only when the build matches the pin.

The build is reproducible: the same sources and toolchain give the same `msacm32.dll`. `component-pins.json` records the file built with MSVC 14.44.35207 and Windows SDK 10.0.26100.0, and `GameplayComponents.Library` in the launcher carries the same pin. A different toolchain or any source change gives a different file; update both pins together.

The build runs two check programs and fails when either does:

- `pocgp_test.c` exercises the component's own logic against a made-up game: which cards a reward may be drawn from in every ownership case, recognising and changing a call instruction in made-up code, deck file selection, and the settings parser.
- `pocgp_proxy_test.c` loads the built library beside the system's and checks that every function the system library exports is exported under the same name and ordinal, and that calls through the component return the system library's answers.

## Settings

The launcher writes `pocgp.ini` (UTF-8) beside the private executable:

| Key | Meaning |
|---|---|
| `new_card_rewards=1` | Switch New card rewards on |
| `opponent_deck=<file>` | A deck file the opponent may use; given once for each ticked deck. A plain file name is looked up beside the component |
| `debug=<n>` | Reserved for more log lines |

## Verification

Launcher checks (`tests/PowerOfChaos.Launcher.Tests/GameplayComponentChecks.cs`) cover the launch preparation with inert fixtures. The component was run in the three supported games on Windows 11, at the repository owner's request, against a copy of a real save that was put back afterwards:

- Yugi the Destiny: a single duel gave one new card three times (32 to 33 cards owned, copies in the save 113 to 116); a match gave three new cards three times each (36 owned, 125 copies). With no card lacking and 32 held once or twice, a duel offered just those and brought one to three copies. With every card held three times, a duel gave one more copy of some card, as the unmodded game does.
- Kaiba the Revenge: a single duel, one new card three times, saved.
- Joey the Passion: a single duel, one new card three times, saved when the reward screen was left.
- Before the three copies were added, an earlier build was also run for a Joey match (three different new cards shown) and for a Yugi match with two cards lacking (those two and one owned card).
- Through the launcher, with that earlier build: Play with only this mod ticked started Yugi the Destiny from a private directory holding the unchanged executable, the component and its settings; with Original aspect ratio ticked as well it started from the display cache with both components loaded. In both cases the log reported the mod on. No duel was played in these two runs. The pinned build was started once more through the launcher together with Original aspect ratio, with the same result.

Custom opponent decks, with the build before the last review:

- Through the launcher: two ticked decks for Yugi the Destiny arrived as two deck files beside the component, and its log reported both in use.
- Yugi the Destiny: against a 42-card deck made only of monsters none of the game's own decks holds, the opponent summoned two of them and attacked with both. The tutorial's practice duel and the title demo asked for no deck file.
- Kaiba the Revenge and Joey the Passion: against a deck of the same kind the opponent summoned its strongest monster on its first turn.
- All three games: the largest deck the launcher allows (80 main cards, 15 Fusion monsters, 15 side cards) was taken without fault; the opponent summoned and attacked from it in the first turns.
- Earlier, a five-card deck was taken in all three games (the opponent lost at its first draw), in single duels and, opened once for the whole match, in matches.

Two independent reviews read the code. The first, before the three copies were added, found no defect in memory handling, calling conventions, the selection or the launcher changes. The second covered the three copies, the deck code of the launcher and the release packaging, compared every address and hash of `pocgp_games.c` with the three executables, and found no defect in the component; its findings in the launcher's deck window and archive reader were fixed. One change to the component came out of it: the reward stand-ins pass straight through until all three are in place.

The pinned build, which has that change, was run for one more single duel in Yugi the Destiny against a deck file: the deck was handed over, the reward was a new card in three copies, and it was saved. Both check programs of the build passed with it.

## Known limits

- Only the three executable revisions above. On any other the game runs unmodded.
- Not run: a match in Kaiba the Revenge, a match in Joey the Passion with the three copies, and the nearly complete and complete cases in Kaiba and Joey (the logic is shared and covered by the made-up game).
- The game's reward screen shows each card once; nothing on it says that three copies were given.
- During the test duels the games never called the audio compression library, so its pass-through is verified by the check program, not by a game.
- A program that inspects libraries beside executables may ask about an `msacm32.dll` it does not know. The file is the pinned one from this recipe.
- Wine has not been tested. The launcher asks Wine to load the component and then its own built-in library.
- How the opponent plays Spell and Trap cards it never had in its own decks was not examined, and no duel against a custom deck was played to its end.
- Only Joey the Passion's own opponent decks have a side deck, and none of the games' own decks has more than 45 main cards. The largest deck the launcher allows was only run for the first turns of a duel (see Verification); what the opponent does with a side deck in a match was not examined.
- Deck files are opened by an ANSI path: the launcher refuses a private directory outside ASCII or too long for that when decks are in use.
