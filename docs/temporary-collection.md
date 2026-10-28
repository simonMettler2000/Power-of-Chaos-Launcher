# Temporary collection transactions

Temporary all cards is an explicitly activated Windows runtime feature. During development, original executables, archives, saves and registry values remain read-only; automated tests use launcher-authored synthetic structures and fake processes.

## Profile boundary

The unit of restoration is the complete stock `system.dat`, not a list of owned card IDs. This preserves quantities, deck flags, identity, statistics and other fields exactly. Three total owned copies are granted at the internal slots read from each added game's own supported card tables. The cumulative pools contain 155, 466 and 771 cards. Slots outside those pools are preserved. The derived total-copy count and both checksums are recalculated; unsupported encoding and overflow fail closed. No card database or game payload is bundled.

The supported native games identify a profile by clear header byte0 and DWORD4–7. Header bytes1–3 are padding and can change during an ordinary game save. Active-profile checks use the same semantic identity fields, so those padding rewrites do not spuriously require recovery. A changed semantic identity still blocks launch; restoration always uses every original byte, including its original padding.

Replay files and exported decks outside `system.dat` are not rolled back. Progress inside the temporary save is archived separately before the original is restored. Backups from previous activation generations remain available and are never reused as a new baseline.

## Location and lifecycle

Automatic CommonDir and filesystem VirtualStore resolution select the active shared save. A manually chosen Collection inspection file is ignored for mutation. Activation requires an unambiguous readable save and valid card data for every added game. An active generation binds its target path and supported slots; changes require restoring the original first.

The authoritative journal is in the fixed per-user local application-data folder `PowerOfChaosLauncher/temporary-collection`, independently of the settings-directory override. It records a version, generation, transaction phase, target path, original and expected SHA256 identities, and supported slot indices. A separate recovery copy is written before the primary copy at each transition. The metadata contains no game content. Original and temporary save files beside it remain private runtime data.

All managed launches acquire the same exclusive file lease before preparing or starting a game and retain it through session tracking. Activation and restoration require that lease and check for all three Windows game process names. The short target-file guard excludes ordinary readers and writers during replacement. These controls cannot coordinate arbitrary external tools that ignore Windows sharing or replace files directly; backups and recovery state remain the source of restoration. No process is killed.

Active state persists across restarts. Each managed active launch restores three quantities in the temporary profile if necessary, preserving a copy of its current contents first. Monitoring cancellation and launcher disposal never restore a save: a game may still be alive. The OS process check remains necessary after a forced launcher exit.

## Durable ordering and recovery

Activation writes and flushes an immutable original backup, then atomically records `enabling`, replaces the target, and records `active`. Restoration archives the current temporary save, records `restoring`, replaces the target with the verified original bytes, and records `inactive`. Files are created with write-through and flushed to disk before replacement. Cancellation before the recorded transition leaves the target alone; an interruption after it leaves an explicit recovery path.

Atomic status reads do not wait on a session's long-lived lease. Known completed enabling replacements can be recognized by their expected hash. A missing, damaged or inconsistent journal copy requires recovery using the retained descriptor; it never silently becomes a fresh off state or an unlocked new baseline. If both copies are unavailable while prior generation evidence exists, the launcher blocks Play for manual backup inspection. Corrupt metadata is preserved. Recovery of a descriptor already marked inactive repairs metadata without reverting subsequent ordinary game progress. Other incomplete transitions require explicit restoration. Damaged or missing original backups, linked paths and unsupported saves block launching or mutation. Current damaged temporary bytes may be archived before a verified original is restored. No uncertain data becomes a new original baseline.

Windows can transiently reject a replacement even during a sequential transaction. A short, bounded retry handles the supported sharing/replacement errors without falling back to an in-place write. An error reported after the expected bytes are already present is checked explicitly. Persistent failure retains the transaction and backups for recovery.

The synthetic checks cover exact byte restoration, all cumulative pools, unrelated-file preservation, interruption boundaries, retained generations, checksum and overflow handling, tampered/missing backups, changed paths, cross-instance exclusion, cancellation, fake-process lifetime and live localized UI state. These checks establish transaction behavior; they do not execute the proprietary games or prove in-game acceptance on every installation.
