/* Gameplay component: shared declarations.

   The component is one authored DLL that the launcher places beside a private copy of a supported game
   executable under the name of a system library the games import (msacm32.dll). It forwards that
   library's functions to the real one and, for the features the launcher asks for, changes where a few
   existing call instructions of the game lead. No game code is stored or rewritten here. */
#ifndef POCGP_H
#define POCGP_H

#include <windows.h>

#define POCGP_MAX_DECKS 64
/* Card slots run from 1 to 0x45A in all three games. */
#define POCGP_SLOTS 0x45B
/* The most copies of one card a deck may hold; a won card is given in that number. */
#define POCGP_COPIES 3

typedef struct POCGP_SETTINGS
{
    int new_card_rewards;
    int debug;
    int deck_count;
    char decks[POCGP_MAX_DECKS][MAX_PATH];   /* paths in the form the games' own file routine takes */
} POCGP_SETTINGS;

/* A stretch of the loaded executable, recognised by where it is and by FNV-1a (64 bit) over its bytes. */
typedef struct POCGP_REGION { DWORD address; DWORD length; unsigned __int64 hash; } POCGP_REGION;

/* A five-byte relative call instruction of the game: where it stands and where it leads. */
typedef struct POCGP_CALL { DWORD site; DWORD target; } POCGP_CALL;

/* What the reward code of a game offers; the same five functions and one table in all three games. */
typedef struct POCGP_REWARD_GAME
{
    int (__cdecl* is_match)(void);                   /* non-zero when the duel was part of a match */
    void (__cdecl* count)(int* owned, int* total);   /* cards of the pool the player owns, cards in the pool */
    unsigned (__cdecl* pack)(int slot);              /* pool bits of a slot, in the low 16 bits */
    unsigned (__cdecl* mask)(void);                  /* pool bit of this game, in the low 16 bits */
    unsigned (__cdecl* add)(unsigned slot);          /* one more copy of a slot (low 16 bits) for the player */
    const BYTE* quantities;                          /* copies owned of slot n: quantities[n * 2] */
} POCGP_REWARD_GAME;

typedef void* (__cdecl* POCGP_OPEN)(const char* path, const char* mode);

/* pocgp_main.c */
void pocgp_log(const char* format, ...);
unsigned pocgp_random(void);

/* pocgp_patch.c */
unsigned __int64 pocgp_hash(const BYTE* bytes, DWORD length);
BOOL pocgp_region_matches(const POCGP_REGION* region);
BOOL pocgp_call_matches(const POCGP_CALL* call);
BOOL pocgp_call_retarget(const POCGP_CALL* call, const void* replacement);

/* pocgp_settings.c */
void pocgp_parse_settings(const char* text, size_t length, const wchar_t* directory, POCGP_SETTINGS* settings);

/* pocgp_rewards.c */
int pocgp_rewards_select(const BYTE* pool, const BYTE* copies, int slots, int amount, unsigned (*random)(void), BYTE* eligible);
void pocgp_rewards_bind(const POCGP_REWARD_GAME* game);
void pocgp_rewards_enable(void);
void __cdecl pocgp_rewards_count(int* owned, int* total);
unsigned __cdecl pocgp_rewards_pack(int slot);
unsigned __cdecl pocgp_rewards_add(unsigned slot);

/* pocgp_decks.c */
BOOL pocgp_decks_is_opponent_deck(const char* path, const char* prefix);
void pocgp_decks_bind(POCGP_OPEN open, const char* prefix, const POCGP_SETTINGS* settings);
void* __cdecl pocgp_decks_open(const char* path, const char* mode);

/* pocgp_games.c */
BOOL pocgp_games_install(const char* executable, const POCGP_SETTINGS* settings);

/* pocgp_proxy.c */
void pocgp_proxy_bind(HMODULE self);

#endif
