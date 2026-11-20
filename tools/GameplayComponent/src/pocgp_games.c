/* Gameplay component: the three supported executables.

   A game is recognised by its file name and by a hash over the code around each place the component
   touches. The table holds addresses, lengths and hashes only. For a feature the launcher did not ask
   for, nothing of the game is changed at all. */
#include "pocgp.h"

typedef struct PROFILE
{
    const char* executable;
    /* Reward code: from its start to the end of its pool test. */
    POCGP_REGION reward;
    POCGP_CALL reward_is_match, reward_count, reward_pack, reward_mask;
    DWORD quantities;
    /* Where the reward code adds a drawn card to the player's cards. */
    POCGP_REGION add;
    POCGP_CALL add_call;
    /* File routine: around the open of a plain path. */
    POCGP_REGION open;
    POCGP_CALL open_call;
    const char* deck_prefix;
} PROFILE;

static const PROFILE g_profiles[] =
{
    { "yugi_pc.exe",
        { 0x00434CC0, 195, 0x5C99790A15B8F49Fui64 },
        { 0x00434CE0, 0x004373C0 }, { 0x00434CF9, 0x0045DD50 }, { 0x00434D71, 0x00401B50 }, { 0x00434D7E, 0x00540410 },
        0x00A1012C,
        { 0x00434E4A, 48, 0xE02223233DA4B009ui64 }, { 0x00434E6A, 0x00540240 },
        { 0x0051A344, 48, 0x201D93697C5C3688ui64 }, { 0x0051A364, 0x0058820C }, "data/y/file/" },
    { "kaiba_pc.exe",
        { 0x00435280, 195, 0x70EE8F88F4518DBDui64 },
        { 0x004352A0, 0x00437970 }, { 0x004352B9, 0x0045FC10 }, { 0x00435331, 0x00401B50 }, { 0x0043533E, 0x00542AE0 },
        0x00A1314C,
        { 0x0043540A, 48, 0x193574D84C40EA26ui64 }, { 0x0043542A, 0x00542910 },
        { 0x0051C6F4, 48, 0x2DC89AADCFEDE234ui64 }, { 0x0051C714, 0x0058A9AC }, "data/k/file/" },
    { "joey_pc.exe",
        { 0x00445920, 180, 0x0B7BBD67F262657Aui64 },
        { 0x0044592D, 0x0044BCA0 }, { 0x00445947, 0x0047D9D0 }, { 0x004459C4, 0x004024E0 }, { 0x004459CF, 0x005BEAA0 },
        0x00A53CCC,
        { 0x00445BAE, 48, 0x397CB88F6307150Dui64 }, { 0x00445BCE, 0x005BE890 },
        { 0x00542A27, 48, 0x32BE4D6BA79F69F3ui64 }, { 0x00542A47, 0x005C5B5B }, "data/j/file/" },
};

static BOOL install_rewards(const PROFILE* profile)
{
    POCGP_REWARD_GAME game;
    MEMORY_BASIC_INFORMATION info;
    const BYTE* quantities = (const BYTE*)(DWORD_PTR)profile->quantities;
    if (!pocgp_region_matches(&profile->reward) || !pocgp_call_matches(&profile->reward_is_match)
        || !pocgp_call_matches(&profile->reward_count) || !pocgp_call_matches(&profile->reward_pack)
        || !pocgp_call_matches(&profile->reward_mask)
        || !pocgp_region_matches(&profile->add) || !pocgp_call_matches(&profile->add_call)) return FALSE;
    /* The quantity table lies in the game's own data; it must be there before anything is changed. */
    if (!VirtualQuery(quantities, &info, sizeof(info)) || info.State != MEM_COMMIT
        || (const BYTE*)info.BaseAddress + info.RegionSize < quantities + POCGP_SLOTS * 2) return FALSE;
    game.is_match = (int (__cdecl*)(void))(DWORD_PTR)profile->reward_is_match.target;
    game.count = (void (__cdecl*)(int*, int*))(DWORD_PTR)profile->reward_count.target;
    game.pack = (unsigned (__cdecl*)(int))(DWORD_PTR)profile->reward_pack.target;
    game.mask = (unsigned (__cdecl*)(void))(DWORD_PTR)profile->reward_mask.target;
    game.add = (unsigned (__cdecl*)(unsigned))(DWORD_PTR)profile->add_call.target;
    game.quantities = quantities;
    pocgp_rewards_bind(&game);
    /* Each stand-in passes straight through until all three are in place, so a call that could not be
       changed leaves the game exactly as it was, whichever of them it is. */
    if (!pocgp_call_retarget(&profile->reward_pack, (const void*)pocgp_rewards_pack)
        || !pocgp_call_retarget(&profile->add_call, (const void*)pocgp_rewards_add)
        || !pocgp_call_retarget(&profile->reward_count, (const void*)pocgp_rewards_count)) return FALSE;
    pocgp_rewards_enable();
    return TRUE;
}

static BOOL install_decks(const PROFILE* profile, const POCGP_SETTINGS* settings)
{
    if (!pocgp_region_matches(&profile->open) || !pocgp_call_matches(&profile->open_call)) return FALSE;
    pocgp_decks_bind((POCGP_OPEN)(DWORD_PTR)profile->open_call.target, profile->deck_prefix, settings);
    return pocgp_call_retarget(&profile->open_call, (const void*)pocgp_decks_open);
}

BOOL pocgp_games_install(const char* executable, const POCGP_SETTINGS* settings)
{
    int i;
    for (i = 0; i < (int)(sizeof(g_profiles) / sizeof(g_profiles[0])); i++)
    {
        const PROFILE* profile = &g_profiles[i];
        if (lstrcmpiA(executable, profile->executable) != 0) continue;
        if (settings->new_card_rewards)
            pocgp_log(install_rewards(profile) ? "%s: new card rewards are on"
                : "%s: the reward code is not that of the supported revision; rewards are left to the game", executable);
        if (settings->deck_count > 0)
            pocgp_log(install_decks(profile, settings) ? "%s: %d opponent deck file(s) in use"
                : "%s: the file routine is not that of the supported revision; the game's own decks are used", executable,
                settings->deck_count);
        return TRUE;
    }
    pocgp_log("%s is not a supported game; nothing was changed", executable);
    return FALSE;
}
