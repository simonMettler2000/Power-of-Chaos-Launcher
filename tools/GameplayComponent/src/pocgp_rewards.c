/* Gameplay component: New card rewards.

   The games give one card for a won duel and three for a won match. They draw each card evenly from
   the cards of their own pool, owned or not, by asking two things of their own code: how many cards
   the pool has, and whether a given card slot belongs to it. Each drawn card is then added once.

   This unit answers those two questions for the reward code only, and stands in for the adding:

   - While the player lacks cards of the pool, the pool it reports holds just those cards.
   - When no card is lacking but some are held in fewer than three copies, the pool holds just those.
   - Every card of a reward is added until the player holds three copies of it, the most a deck takes.
   - When a reward has more cards than the reported pool would hold, cards of the next kind are
     chosen at random to fill it, so that the reward can still consist of different cards.
   - When every card of the pool is held three times or more, the game's answers pass through
     untouched and a card is added once, exactly as without the mod.

   The game's own drawing, its "new" badge, its saving and its screens run unchanged. */
#include "pocgp.h"

static POCGP_REWARD_GAME g_game;
static BYTE g_eligible[POCGP_SLOTS];
static BOOL g_narrowed;
static BOOL g_enabled;

/* Marks up to `wanted` randomly chosen pool slots, not yet marked, that hold `low` to `high` copies. */
static int fill(const BYTE* pool, const BYTE* copies, int slots, int low, int high, int wanted, unsigned (*random)(void), BYTE* eligible)
{
    int slot, available = 0, marked = 0;
    for (slot = 0; slot < slots; slot++)
        if (pool[slot] && !eligible[slot] && copies[slot] >= low && copies[slot] <= high) available++;
    while (marked < wanted && available > 0)
    {
        int pick = (int)(random() % (unsigned)available);
        for (slot = 0; slot < slots; slot++)
        {
            if (!pool[slot] || eligible[slot] || copies[slot] < low || copies[slot] > high) continue;
            if (pick-- == 0) { eligible[slot] = 1; break; }
        }
        marked++;
        available--;
    }
    return marked;
}

/* Marks the slots a reward of `amount` cards may be drawn from and returns their number.
   `copies` holds how many copies of each slot the player owns. Zero means: leave the game's own pool as it is. */
int pocgp_rewards_select(const BYTE* pool, const BYTE* copies, int slots, int amount, unsigned (*random)(void), BYTE* eligible)
{
    int slot, missing = 0, incomplete = 0, complete = 0, offered;
    for (slot = 0; slot < slots; slot++)
    {
        eligible[slot] = 0;
        if (!pool[slot]) continue;
        if (copies[slot] == 0) missing++;
        else if (copies[slot] < POCGP_COPIES) incomplete++;
        else complete++;
    }
    /* Nothing left to give, or a pool that cannot fill the reward with different cards either way. */
    if (amount < 1 || missing + incomplete == 0 || missing + incomplete + complete < amount) return 0;
    /* All cards of the first kind there is: lacking cards before cards held in too few copies. */
    for (slot = 0; slot < slots; slot++)
        if (pool[slot] && (missing > 0 ? copies[slot] == 0 : copies[slot] < POCGP_COPIES)) eligible[slot] = 1;
    offered = missing > 0 ? missing : incomplete;
    if (offered < amount && missing > 0)
        offered += fill(pool, copies, slots, 1, POCGP_COPIES - 1, amount - offered, random, eligible);
    if (offered < amount)
        offered += fill(pool, copies, slots, POCGP_COPIES, 255, amount - offered, random, eligible);
    return offered;
}

void pocgp_rewards_bind(const POCGP_REWARD_GAME* game)
{
    g_game = *game;
    g_narrowed = FALSE;
    g_enabled = FALSE;
}

/* The stand-ins act only once all of them are in place. Until then, and if one of them could not be
   put in place, each passes straight through to the game's own function. */
void pocgp_rewards_enable(void)
{
    g_enabled = TRUE;
}

/* Stands in for the game's pool count at the start of its reward code. */
void __cdecl pocgp_rewards_count(int* owned, int* total)
{
    static BYTE pool[POCGP_SLOTS], copies[POCGP_SLOTS];
    unsigned mask;
    int slot, amount, eligible, owned_before, total_before;
    g_narrowed = FALSE;
    g_game.count(owned, total);
    if (!g_enabled) return;
    owned_before = *owned;
    total_before = *total;
    mask = g_game.mask() & 0xFFFF;
    pool[0] = copies[0] = 0;
    for (slot = 1; slot < POCGP_SLOTS; slot++)
    {
        pool[slot] = (BYTE)((g_game.pack(slot) & mask) != 0);
        copies[slot] = g_game.quantities[slot * 2];
    }
    amount = g_game.is_match() ? 3 : 1;
    eligible = pocgp_rewards_select(pool, copies, POCGP_SLOTS, amount, pocgp_random, g_eligible);
    if (eligible > 0)
    {
        /* The game draws "random modulo total" and takes that many pool slots in order, so the total
           must be exactly the number of slots the pool test below lets through. */
        *total = eligible;
        *owned = 0;
        g_narrowed = TRUE;
    }
    pocgp_log("reward of %d: the player owns %d of %d cards in the pool; %s (%d to draw from)", amount, owned_before, total_before,
        eligible <= 0 ? "left to the game" : owned_before < total_before ? "only lacking cards are offered"
            : "only cards held in fewer than three copies are offered", eligible > 0 ? eligible : total_before);
}

/* Stands in for the game's pool lookup inside its reward drawing. */
unsigned __cdecl pocgp_rewards_pack(int slot)
{
    unsigned value = g_game.pack(slot) & 0xFFFF;
    if (g_narrowed && (slot < 1 || slot >= POCGP_SLOTS || !g_eligible[slot])) return 0;
    return value;
}

/* Stands in for the game's adding of one reward card: the game's own routine, called until the
   player holds three copies. A card already held three times or more is added once, as always. */
unsigned __cdecl pocgp_rewards_add(unsigned slot)
{
    unsigned result = g_game.add(slot);
    unsigned index = slot & 0xFFFF;
    int calls;
    if (!g_enabled || index < 1 || index >= POCGP_SLOTS) return result;
    for (calls = 1; calls < POCGP_COPIES && g_game.quantities[index * 2] < POCGP_COPIES; calls++) g_game.add(slot);
    pocgp_log("reward card in slot %u: %u copies now", index, (unsigned)g_game.quantities[index * 2]);
    return result;
}
