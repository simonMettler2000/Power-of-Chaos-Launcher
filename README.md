# Power of Chaos Launcher

Launch **Yugi the Destiny**, **Kaiba the Revenge** and **Joey the Passion** from one place. Track your playtime, browse your card collection and choose built-in mods.

You need your own installed copies of the games. The launcher does not include the games or their original artwork, audio or saves.

## Download and start playing

**[Download the Windows launcher](https://github.com/simonMettler2000/Power-of-Chaos-Launcher/releases/tag/v0.2.0)**

1. Choose **[win-x64](https://github.com/simonMettler2000/Power-of-Chaos-Launcher/releases/download/v0.2.0/PowerOfChaos-Launcher-0.2.0-win-x64.zip)** for most Windows PCs, or **[win-x86](https://github.com/simonMettler2000/Power-of-Chaos-Launcher/releases/download/v0.2.0/PowerOfChaos-Launcher-0.2.0-win-x86.zip)** for 32-bit Windows.
2. Extract the **entire ZIP** into a new folder. Keep all its files together.
3. Open **PowerOfChaos.Launcher.exe**.
4. Open **Settings → Add Game path**. Select a game's installation folder, or a parent folder containing all three games. Click **Find games** to add the detected games.
5. Click **Play** beside the game you want to launch.

The Windows download includes the .NET runtime and components needed for the available mods. You do not need to install .NET, a development SDK or a separate mod bundle.

Use the launcher ZIP, rather than GitHub's **Source code** downloads or the separate component/source archives. The **HD artwork** release is a separate download and is not a launcher installer.

Windows 10/11 editions supported by .NET 10 are the target. Windows 7/8.1 and native ARM64 game compatibility are not established. Linux/macOS users can build the launcher from source and configure a Wine runner; game compatibility depends on the platform and runner.

## What you can do

- Launch any of the three games you have added.
- See playtime and card counts beside each game's **Play** button.
- Browse owned and missing cards in **Collection**.
- Choose English, German, French, Italian, Spanish or Japanese under **Language**, beneath Settings. This changes the launcher interface and available card names, without changing your game files or the game's own language.
- Enable the available mods in **Mods**.

Settings and playtime are saved locally. No account is required. Playtime counts sessions started through this launcher, including time in menus; it does not recover hours played before you used the launcher.

## Your card collection

| Game | Available cards |
| --- | ---: |
| Yugi the Destiny | 155 |
| Kaiba the Revenge | 466 |
| Joey the Passion | 771 |
| **Unique cards across all three** | **771** |

These are cumulative card pools: later games include cards from earlier games. Owning several copies of a card, or having it available in several games, adds only one to the unique total.

Expand a game in **Collection** to see the cards you own and the cards you are missing. The gallery uses small original images from your installed games, so **HD Cards is not required**. Hover over a card for its name, copy count and the first game where it is available. Clicking a card does not enlarge it.

The stock games share a collection save named **system.dat**. The launcher tries to find it automatically. If the count shows **—/155**, **—/466** or **—/771**, open Collection and select or paste the path to your save, then refresh. A dash means the save could not be read; it does not mean you own zero cards. A total based on only some readable games is marked as partial.

Browsing Collection does not change your save. Names come from the language tables in your installed games; an unavailable name falls back to the card ID. Custom saves or modified card tables may not be supported. [More about collection detection](docs/collection.md).

## Available mods

These four mods are available in the stable Windows download:

| Mod | What it does |
| --- | --- |
| **Skip Splash Screens** | Skips the publisher logos and opens the normal title menu. |
| **Skip character interactions** | Skips duel greetings, character dialogue and reaction cut-ins. Keeps card effects, duel music and the result presentation. |
| **Original aspect ratio** | Keeps the game's 4:3 picture when resizing or using borderless fullscreen. Use **Alt+Enter** to switch fullscreen mode. |
| **Temporary all cards** | Temporarily grants three copies of each card in your added games' supported pools, with an original-save backup for restoration. |

Executable mods require a supported game revision. The launcher checks compatibility before preparing a private launch copy and leaves installed executables and archives unchanged. If a revision is unsupported, untick those mods to launch normally. Changes to launch mods take effect on the next launch. Skipping character interactions can change the sequence of random choices compared with an unmodified run.

[Supported versions and mod details](docs/mods.md) · [Display options](docs/display.md)

### Before using Temporary all cards

**This mod temporarily replaces your shared save. Progress made while it is active belongs to the temporary profile.**

1. Close all three games.
2. Tick **Temporary all cards** in Mods. The launcher backs up the complete original save before unlocking cards.
3. When you want your normal profile back, close all games and untick the mod.

Restoration returns your original cards, decks, statistics and other save fields. **Progress made while the mod was active is not merged into your original profile.** The last temporary save is archived separately. Closing the launcher alone does not turn the mod off; its state survives launcher restarts.

Keep the backup directory: `%LOCALAPPDATA%/PowerOfChaosLauncher/temporary-collection`. Restore your normal profile before changing game locations. If the launcher reports an interrupted transaction, use **Restore original collection** when offered. Do not delete the backups to bypass a recovery message.

This feature requires Windows, a supported card table and an existing shared save that the launcher can identify and write. Choosing a different save just for Collection browsing does not change the mod's write target. [Backup and recovery details](docs/temporary-collection.md).

## Features still in development

**HD Cards, opponent-deck packs and guaranteed-new rewards are disabled in the stable launcher.** Local development builds can differ from the public download.

A separate [HD card artwork pack](https://github.com/simonMettler2000/Power-of-Chaos-Launcher/releases/tag/hd-card-artwork-v1) is available for compatible experimental builds. Artwork alone does not enable HD rendering. Full HD coverage for cards in hand, on the field, in deck/graveyard views and during animations is not a released feature.

Future built-in mods will arrive through launcher updates. [Release and extension workflow](docs/releases.md).

## Troubleshooting

- **Game not found:** Select the installed game folder or a nearby parent. Discovery searches up to six folder levels below your selection and needs the expected executable, Data.dat and Voice.dat files.
- **Collection count is a dash:** Choose your shared system.dat in Collection and refresh. Missing, busy or unsupported saves cannot produce a reliable count.
- **A mod refuses to launch:** Check the supported versions above. Finding a game folder does not guarantee mod compatibility. Try with the executable/display mods unticked.
- **Files missing after extracting:** Extract the whole launcher ZIP again into a new folder. Do not move only the EXE out of the package.

For a reproducible bug, [open an issue](https://github.com/simonMettler2000/Power-of-Chaos-Launcher/issues) with the launcher version, Windows version, affected game, enabled mods and full error text. Please do not attach game executables, archives or personal saves.

## For developers

The launcher is written in C# with Avalonia. Use the .NET SDK pinned in [global.json](global.json).

```sh
dotnet restore PowerOfChaos.Launcher.sln --locked-mode
dotnet build PowerOfChaos.Launcher.sln -c Release --no-restore
dotnet run --project src/PowerOfChaos.Launcher -c Release --no-build
```

Source builds need the appropriate components for display mods; see [display setup](docs/display.md) and [Windows packaging](docs/releases.md).

```sh
dotnet run --project tests/PowerOfChaos.Launcher.Tests -c Release
dotnet run --project tests/PowerOfChaos.Launcher.UiChecks -c Release
python tools/publication_guard.py --working-tree
```

Development tests use synthetic fixtures and fake game processes. They do not execute the original games. Automated checks do not establish gameplay compatibility for every installation or Wine runner.

Keep game files, saves, extracted assets, local settings and build outputs out of Git. Run the publication guard against the working tree and exact commit before publishing. See [publication rules](docs/publication.md) for the full boundary.

## License

The authored launcher code is [MIT licensed](LICENSE). Dependencies retain their own licenses and notices. The separate HD fan-art pack has its own notice and is not covered by the launcher's MIT license.

This is an independent fan project, not an official KONAMI product.
