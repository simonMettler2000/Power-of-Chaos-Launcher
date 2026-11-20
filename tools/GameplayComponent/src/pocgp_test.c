/* Gameplay component: checks of its own logic against made-up data. No game, no game data.

   Built together with the component's units (all but the start-up and proxy units) into a small
   console program by Build-GameplayComponent.ps1. A made-up "game" of four functions and a quantity
   table stands in for the real ones. */
#include <stdio.h>
#include <stdarg.h>
#include "pocgp.h"

static int g_failures;
static unsigned g_seed = 12345;
static int g_log_lines;

void pocgp_log(const char* format, ...) { (void)format; g_log_lines++; }
unsigned pocgp_random(void) { g_seed = g_seed * 1103515245u + 12345u; return (g_seed >> 8) & 0x7FFFFF; }

static void check(BOOL condition, const char* what)
{
    if (!condition) { g_failures++; printf("FAILED: %s\n", what); }
}

/* --- a made-up game --------------------------------------------------------------------------- */
static unsigned short g_pack[POCGP_SLOTS];
static BYTE g_quantities[POCGP_SLOTS * 2];
static int g_match;
static unsigned g_mask = 2;
static int g_adds;
static BOOL g_add_stuck;

static int __cdecl fake_is_match(void) { return g_match; }
static unsigned __cdecl fake_pack(int slot) { return 0xABCD0000u | g_pack[slot]; }   /* upper half is noise */
static unsigned __cdecl fake_mask(void) { return 0x12340000u | g_mask; }
static void __cdecl fake_count(int* owned, int* total)
{
    int slot;
    *owned = *total = 0;
    for (slot = 1; slot < POCGP_SLOTS; slot++)
        if (g_pack[slot] & g_mask) { (*total)++; if (g_quantities[slot * 2]) (*owned)++; }
}
/* One more copy, up to 255, as the games do it; the upper half of the argument is noise there too. */
static unsigned __cdecl fake_add(unsigned slot)
{
    slot &= 0xFFFF;
    g_adds++;
    if (!g_add_stuck && slot < POCGP_SLOTS && g_quantities[slot * 2] < 255) g_quantities[slot * 2]++;
    return 0x7E57;
}

/* Pool of `size` cards from slot 10 on: first `missing` cards not owned, then `incomplete` cards held once or twice,
   the rest held three times or more. Cards of another pool lie around it. */
static void make_game(int size, int missing, int incomplete)
{
    int slot;
    memset(g_pack, 0, sizeof(g_pack));
    memset(g_quantities, 0, sizeof(g_quantities));
    for (slot = 1; slot < 10; slot++) g_pack[slot] = 4;
    for (slot = 10; slot < 10 + size; slot++)
    {
        int index = slot - 10;
        g_pack[slot] = 6;
        g_quantities[slot * 2] = (BYTE)(index < missing ? 0 : index < missing + incomplete ? 1 + (slot & 1) : 3 + (slot % 3));
        g_quantities[slot * 2 + 1] = 0xEE;   /* the neighbouring byte is not a quantity */
    }
}

static void check_reward(int size, int missing, int incomplete, int match, const char* name)
{
    int reported_owned = -1, reported_total = -1, slot, offered = 0, offered_missing = 0, offered_incomplete = 0;
    int amount = match ? 3 : 1;
    char what[200];
    make_game(size, missing, incomplete);
    g_match = match;
    pocgp_rewards_count(&reported_owned, &reported_total);
    for (slot = 0; slot < POCGP_SLOTS; slot++)
    {
        unsigned value = pocgp_rewards_pack(slot);
        if ((value & g_mask) == 0) continue;
        offered++;
        if (g_quantities[slot * 2] == 0) offered_missing++;
        else if (g_quantities[slot * 2] < POCGP_COPIES) offered_incomplete++;
        if (!(g_pack[slot] & g_mask)) { sprintf(what, "%s: a card outside the pool is offered", name); check(FALSE, what); }
    }
    sprintf(what, "%s: the reported total is the number of offered cards", name);
    check(reported_total == offered, what);
    if (missing + incomplete == 0 || size < amount)
    {
        sprintf(what, "%s: the game's own pool is untouched", name);
        check(offered == size && reported_owned == size - missing, what);
    }
    else if (missing >= amount)
    {
        sprintf(what, "%s: exactly the lacking cards are offered", name);
        check(offered == missing && offered_missing == missing && reported_owned == 0, what);
    }
    else if (missing > 0)
    {
        int more = amount - missing, from_incomplete = more < incomplete ? more : incomplete;
        sprintf(what, "%s: the lacking cards, then cards held in too few copies, then others fill the reward", name);
        check(offered == amount && offered_missing == missing && offered_incomplete == from_incomplete && reported_owned == 0, what);
    }
    else if (incomplete >= amount)
    {
        sprintf(what, "%s: exactly the cards held in too few copies are offered", name);
        check(offered == incomplete && offered_incomplete == incomplete && reported_owned == 0, what);
    }
    else
    {
        sprintf(what, "%s: the cards held in too few copies and just enough others are offered", name);
        check(offered == amount && offered_incomplete == incomplete && reported_owned == 0, what);
    }
}

static void check_add(int before, int after, int calls, const char* name)
{
    char what[160];
    unsigned result;
    g_quantities[40 * 2] = (BYTE)before;
    g_quantities[40 * 2 + 1] = 0xEE;
    g_quantities[41 * 2] = 1;
    g_adds = 0;
    result = pocgp_rewards_add(0xBEEF0000u | 40);
    sprintf(what, "%s: %d copies become %d with %d call(s) of the game's routine", name, before, after, calls);
    check(g_quantities[40 * 2] == after && g_adds == calls && result == 0x7E57 && g_quantities[40 * 2 + 1] == 0xEE && g_quantities[41 * 2] == 1, what);
}

static void check_rewards(void)
{
    static const BYTE pool[8] = { 0, 1, 1, 1, 1, 0, 1, 1 };
    static const BYTE spread[8] = { 0, 3, 0, 4, 3, 0, 9, 3 };       /* one card lacking, five held often enough */
    static const BYTE ordered[8] = { 0, 1, 0, 3, 2, 0, 3, 3 };      /* one lacking, two held too rarely */
    POCGP_REWARD_GAME game;
    BYTE eligible[8];
    int round, counts[8] = { 0 };
    game.is_match = fake_is_match;
    game.count = fake_count;
    game.pack = fake_pack;
    game.mask = fake_mask;
    game.add = fake_add;
    game.quantities = g_quantities;
    pocgp_rewards_bind(&game);
    /* Until every stand-in is in place each one passes straight through. */
    make_game(155, 55, 40);
    g_match = 1;
    {
        int a = -1, b = -1;
        pocgp_rewards_count(&a, &b);
        check(a == 100 && b == 155 && (pocgp_rewards_pack(100) & 0xFFFF) == 6 && g_log_lines == 0, "a stand-in that is not switched on changes no answer");
        g_adds = 0;
        check(pocgp_rewards_add(10) == 0x7E57 && g_adds == 1 && g_quantities[10 * 2] == 1, "a stand-in that is not switched on adds a card once");
    }
    pocgp_rewards_enable();
    check_reward(155, 155, 0, 0, "nothing owned, duel");
    check_reward(155, 155, 0, 1, "nothing owned, match");
    check_reward(155, 55, 40, 0, "partly owned, duel");
    check_reward(155, 55, 40, 1, "partly owned, match");
    check_reward(155, 3, 0, 1, "three lacking, match");
    check_reward(155, 2, 0, 1, "two lacking and no card held too rarely, match");
    check_reward(155, 2, 9, 1, "two lacking, match");
    check_reward(155, 1, 1, 1, "one lacking and one held too rarely, match");
    check_reward(155, 1, 0, 1, "one lacking, match");
    check_reward(155, 1, 30, 0, "one lacking, duel");
    check_reward(155, 0, 30, 0, "none lacking, some held too rarely, duel");
    check_reward(155, 0, 30, 1, "none lacking, some held too rarely, match");
    check_reward(155, 0, 2, 1, "none lacking, two held too rarely, match");
    check_reward(155, 0, 1, 0, "none lacking, one held too rarely, duel");
    check_reward(155, 0, 0, 0, "all held three times, duel");
    check_reward(155, 0, 0, 1, "all held three times, match");
    check_reward(771, 1, 0, 1, "large pool, one lacking, match");
    check_reward(2, 1, 1, 1, "pool smaller than the reward");
    check_reward(0, 0, 0, 0, "empty pool");
    /* After a reward that was left to the game, the pool test lets everything of the pool through. */
    make_game(155, 0, 0);
    g_match = 0;
    {
        int a, b;
        pocgp_rewards_count(&a, &b);
        check((pocgp_rewards_pack(10) & 0xFFFF) == 6 && (pocgp_rewards_pack(5) & 0xFFFF) == 4, "pool values pass through when nothing is narrowed");
        check(pocgp_rewards_pack(0) == 0, "slot zero is never offered");
    }
    /* While the pool is narrowed, slot zero and owned slots give nothing, whatever the game's table holds. */
    make_game(155, 55, 0);
    g_pack[0] = 6;
    {
        int a, b;
        pocgp_rewards_count(&a, &b);
        check(b == 55 && pocgp_rewards_pack(0) == 0 && pocgp_rewards_pack(100) == 0 && (pocgp_rewards_pack(20) & 0xFFFF) == 6
            && (pocgp_rewards_pack(5) & g_mask) == 0, "a narrowed pool offers the lacking cards only");
    }
    /* The cards that fill a reward are spread over all cards of their kind. */
    for (round = 0; round < 4000; round++)
    {
        int slot, chosen = pocgp_rewards_select(pool, spread, 8, 3, pocgp_random, eligible);
        check(chosen == 3 && eligible[2] == 1 && eligible[0] == 0 && eligible[5] == 0, "selection keeps the lacking card and stays in the pool");
        for (slot = 0; slot < 8; slot++) counts[slot] += eligible[slot];
    }
    check(counts[1] > 1300 && counts[3] > 1300 && counts[4] > 1300 && counts[6] > 1300 && counts[7] > 1300
        && counts[1] + counts[3] + counts[4] + counts[6] + counts[7] == 8000, "fill cards are chosen evenly");
    for (round = 0; round < 200; round++)
        check(pocgp_rewards_select(pool, ordered, 8, 3, pocgp_random, eligible) == 3 && eligible[2] && eligible[1] && eligible[4]
            && !eligible[3] && !eligible[6] && !eligible[7], "cards held too rarely fill a reward before cards held often enough");
    check(pocgp_rewards_select(pool, ordered, 8, 1, pocgp_random, eligible) == 1 && eligible[2] && !eligible[1] && !eligible[4],
        "a lacking card comes before a card held too rarely");
    check(pocgp_rewards_select(pool, spread, 8, 0, pocgp_random, eligible) == 0, "a reward of nothing changes nothing");
    /* A won card is added until three copies are held; a card held often enough is added once, as the game does. */
    make_game(155, 0, 0);
    check_add(0, 3, 3, "a new card");
    check_add(1, 3, 2, "a card held once");
    check_add(2, 3, 1, "a card held twice");
    check_add(3, 4, 1, "a card held three times");
    check_add(7, 8, 1, "a card held often");
    check_add(255, 255, 1, "a card at the game's limit");
    g_add_stuck = TRUE;
    check_add(0, 0, 3, "a routine that adds nothing is not called without end");
    g_add_stuck = FALSE;
    g_adds = 0;
    pocgp_rewards_add(0);
    pocgp_rewards_add(0x00010000u);
    pocgp_rewards_add(POCGP_SLOTS);
    pocgp_rewards_add(0xFFFF);
    check(g_adds == 4, "a slot outside the table is passed on once and not looked up");
}

/* --- call instructions ------------------------------------------------------------------------ */
static int __cdecl first_target(void) { return 1; }
static int __cdecl second_target(void) { return 2; }

static void check_patch(void)
{
    static const BYTE sample[] = { 'p', 'o', 'c', 'g', 'p' };
    BYTE* code = (BYTE*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    POCGP_CALL call, wrong;
    POCGP_REGION region;
    DWORD distance, previous;
    int (__cdecl* run)(void);
    check(pocgp_hash(sample, 0) == 0xCBF29CE484222325ui64, "hash of nothing");
    check(pocgp_hash((const BYTE*)"a", 1) == 0xAF63DC4C8601EC8Cui64, "hash of one byte");
    if (!code) { check(FALSE, "memory for the made-up call"); return; }
    /* A made-up routine: one relative call and a return. */
    code[0] = 0xE8;
    distance = (DWORD)(DWORD_PTR)first_target - ((DWORD)(DWORD_PTR)code + 5);
    memcpy(code + 1, &distance, 4);
    code[5] = 0xC3;
    run = (int (__cdecl*)(void))code;
    call.site = (DWORD)(DWORD_PTR)code;
    call.target = (DWORD)(DWORD_PTR)first_target;
    region.address = call.site;
    region.length = 6;
    region.hash = pocgp_hash(code, 6);
    check(pocgp_region_matches(&region), "a region is recognised");
    region.hash ^= 1;
    check(!pocgp_region_matches(&region), "a different hash is refused");
    region.hash ^= 1;
    region.length = 4096 * 4;
    check(!pocgp_region_matches(&region), "a region that runs out of memory is refused");
    region.address = 16;
    region.length = 6;
    check(!pocgp_region_matches(&region), "an unmapped region is refused");
    check(pocgp_call_matches(&call) && run() == 1, "a call is recognised");
    wrong = call;
    wrong.target = (DWORD)(DWORD_PTR)second_target;
    check(!pocgp_call_matches(&wrong) && !pocgp_call_retarget(&wrong, (const void*)first_target), "a call that leads elsewhere is left alone");
    wrong = call;
    wrong.site += 1;
    check(!pocgp_call_matches(&wrong), "something that is not a call is refused");
    /* Read-only code, as in a loaded executable. */
    VirtualProtect(code, 4096, PAGE_EXECUTE_READ, &previous);
    check(pocgp_call_retarget(&call, (const void*)second_target) && run() == 2, "a call is led to its stand-in");
    check(code[0] == 0xE8 && code[5] == 0xC3, "only the distance of the call changed");
    check(!pocgp_call_matches(&call), "a call that was already changed is not changed twice");
    {
        MEMORY_BASIC_INFORMATION info;
        VirtualQuery(code, &info, sizeof(info));
        check(info.Protect == PAGE_EXECUTE_READ, "the protection of the code is restored");
    }
    VirtualFree(code, 0, MEM_RELEASE);
}

/* --- deck files -------------------------------------------------------------------------------- */
static const char* g_opened[8];
static int g_opens;
static const char* g_refused;

static void* __cdecl fake_open(const char* path, const char* mode)
{
    (void)mode;
    if (g_opens < 8) g_opened[g_opens] = path;
    g_opens++;
    return g_refused && strcmp(path, g_refused) == 0 ? NULL : (void*)path;
}

static void check_decks(void)
{
    static POCGP_SETTINGS settings;
    const char* prefix = "data/y/file/";
    int i, first = 0, second = 0;
    check(pocgp_decks_is_opponent_deck("data/Y/file/first.ydc", prefix), "a stock deck path");
    check(pocgp_decks_is_opponent_deck("DATA\\y\\FILE\\SeCoNd.YDC", prefix), "case and slashes do not matter");
    check(!pocgp_decks_is_opponent_deck("deck.ydc", prefix), "the player's deck");
    check(!pocgp_decks_is_opponent_deck("data/Y/file/other.txt", prefix), "another file of the folder");
    check(!pocgp_decks_is_opponent_deck("data/Y/file/sub/first.ydc", prefix), "a file further down");
    check(!pocgp_decks_is_opponent_deck("data/Y/file/.ydc", prefix), "no name");
    check(!pocgp_decks_is_opponent_deck("data/K/file/first.ydc", prefix), "another game's folder");
    check(!pocgp_decks_is_opponent_deck("data/Y/file", prefix) && !pocgp_decks_is_opponent_deck("", prefix)
        && !pocgp_decks_is_opponent_deck(NULL, prefix), "short and missing paths");
    pocgp_parse_settings("opponent_deck=one.ydc\nopponent_deck=two.ydc\n", 44, L"C:\\cache\\", &settings);
    check(settings.deck_count == 2 && strcmp(settings.decks[0], "C:\\cache\\one.ydc") == 0, "deck files are looked up beside the component");
    pocgp_decks_bind(fake_open, prefix, &settings);
    g_opens = 0;
    check(pocgp_decks_open("data.dat", "rb") == (void*)g_opened[0] && g_opens == 1 && strcmp(g_opened[0], "data.dat") == 0, "other files are opened as asked");
    for (i = 0; i < 400; i++)
    {
        char path[64];
        const char* opened;
        sprintf(path, "data/Y/file/duel%02d.ydc", i);   /* a new path each time: a new duel */
        g_opens = 0;
        opened = (const char*)pocgp_decks_open(path, "rb");
        if (opened == settings.decks[0]) first++;
        else if (opened == settings.decks[1]) second++;
        check(g_opens == 1, "one open for a deck");
    }
    check(first + second == 400 && first > 120 && second > 120, "each deck file gets its turn");
    {
        const char* a = (const char*)pocgp_decks_open("data/Y/file/again.ydc", "rb");
        for (i = 0; i < 20; i++)
            check(pocgp_decks_open("data/y/file/AGAIN.ydc", "rb") == (void*)a, "the same deck within one duel set-up");
    }
    g_refused = settings.decks[0];
    settings.deck_count = 1;
    g_opens = 0;
    check(strcmp((const char*)pocgp_decks_open("data/Y/file/gone.ydc", "rb"), "data/Y/file/gone.ydc") == 0 && g_opens == 2,
        "a deck file that cannot be opened falls back to the game's deck");
    /* Within that set-up the game's deck stays: the file is not tried a second time. */
    g_opens = 0;
    check(strcmp((const char*)pocgp_decks_open("data/Y/file/gone.ydc", "rb"), "data/Y/file/gone.ydc") == 0 && g_opens == 1,
        "a set-up that fell back keeps the game's deck");
    g_refused = NULL;
    check(strcmp((const char*)pocgp_decks_open("data/Y/file/next.ydc", "rb"), settings.decks[0]) == 0, "the next duel tries the deck file again");
    settings.deck_count = 0;
    g_opens = 0;
    check(strcmp((const char*)pocgp_decks_open("data/Y/file/first.ydc", "rb"), "data/Y/file/first.ydc") == 0 && g_opens == 1,
        "without deck files nothing is replaced");
}

/* --- settings ---------------------------------------------------------------------------------- */
static void check_settings(void)
{
    static POCGP_SETTINGS settings;
    static const char text[] = "\xEF\xBB\xBF; comment\r\nnew_card_rewards=1\r\ndebug=2\r\nunknown=5\r\nopponent_deck=C:\\decks\\a b.ydc\r\n"
        "opponent_deck=..\\up.ydc\r\nopponent_deck=\r\nopponent_deck=sub/x.ydc\r\nno equals sign\r\nopponent_deck=last.ydc";
    char many[POCGP_MAX_DECKS * 24 + 256];
    int i, length = 0;
    pocgp_parse_settings(text, sizeof(text) - 1, L"D:\\x\\", &settings);
    check(settings.new_card_rewards == 1 && settings.debug == 2, "switches are read");
    check(settings.deck_count == 2 && strcmp(settings.decks[0], "C:\\decks\\a b.ydc") == 0
        && strcmp(settings.decks[1], "D:\\x\\last.ydc") == 0, "full paths and plain names are taken, others refused");
    pocgp_parse_settings("new_card_rewards=0\n", 19, L"D:\\x\\", &settings);
    check(settings.new_card_rewards == 0 && settings.deck_count == 0, "off is off");
    pocgp_parse_settings("", 0, L"D:\\x\\", &settings);
    check(settings.new_card_rewards == 0 && settings.deck_count == 0 && settings.debug == 0, "an empty file switches nothing on");
    for (i = 0; i < POCGP_MAX_DECKS + 8; i++) length += sprintf(many + length, "opponent_deck=d%d.ydc\n", i);
    pocgp_parse_settings(many, (size_t)length, L"D:\\x\\", &settings);
    check(settings.deck_count == POCGP_MAX_DECKS, "no more deck files than there is room for");
    /* A name the system code page cannot hold is refused, not approximated. */
    pocgp_parse_settings("opponent_deck=\xE2\x98\x83.ydc\n", 22, L"D:\\x\\", &settings);
    check(settings.deck_count == 0 || strchr(settings.decks[0], '?') == NULL, "no approximated file names");
    {
        char longname[MAX_PATH * 2];
        memset(longname, 'a', sizeof(longname));
        memcpy(longname, "opponent_deck=", 14);
        longname[sizeof(longname) - 1] = '\n';
        pocgp_parse_settings(longname, sizeof(longname), L"D:\\x\\", &settings);
        check(settings.deck_count == 0, "an overlong name is refused");
    }
}

int main(void)
{
    check_rewards();
    check_patch();
    check_decks();
    check_settings();
    if (g_failures) { printf("%d check(s) failed\n", g_failures); return 1; }
    printf("gameplay component checks passed\n");
    return 0;
}
