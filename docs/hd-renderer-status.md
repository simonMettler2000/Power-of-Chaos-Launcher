# HD card renderer development status

All-card HD is still in development for Yugi the Destiny, Kaiba the Revenge and Joey the Passion. The stable launcher keeps HD Cards disabled. Some experimental builds have HD previews. The goal is to render cards at higher resolution in previews, hands, on the field, and in deck and graveyard views while preserving face-down cards and the player's aspect ratio.

## What passed offline

The latest private Duel diagnostics can follow completed Windows GDI text calls and, on qualifying frames, a CPU shading operation between the canvas unlock and the final copy to the display surface. The team also traced how each title chooses and clips its cursor sprite.

Two independent reviewers checked the exact private diagnostic build. Debug and Release each passed 560 offline cases, with 60 additional review cases in each configuration. The installed games were inspected read-only. Gameplay testing is still pending.

## What remains before gameplay testing

- Capture the selected cursor sprite, final clip and completed pixel writes during the actual draw. A fully clipped draw and a draw that writes the same colors can leave identical pixels in the original frame. The HD renderer still needs to know which pixels the second draw covers.
- Account for writes through the canvas pointer after unlock and carry them in order through Duel's final copy. A review case showed that a later write can reach the display without appearing in the current record.
- Verify each title path offline before enabling a private gameplay build. Preserve ordinary rendering for frames with incomplete records to protect the cursor and other foreground pixels.
