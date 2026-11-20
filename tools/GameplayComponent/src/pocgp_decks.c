/* Gameplay component: handing the game a deck file of the launcher's instead of a stock opponent deck.

   The games read every file through one routine that first tries the plain path and only then looks
   into their archive. Opponent decks are the ".ydc" files of one archive folder. This unit stands in
   for the plain-path open: when the game asks for one of those files, it opens one of the deck files
   named in the settings with the game's own open routine, so the game reads it exactly as it would
   read a loose file. Every other path is passed on untouched. */
#include "pocgp.h"

static POCGP_OPEN g_open;
static const char* g_prefix;
static const POCGP_SETTINGS* g_settings;

/* The game may open the same deck more than once while it sets a duel up; it must get one deck. */
#define SAME_DUEL_MILLISECONDS 3000
static char g_last_path[MAX_PATH];
static DWORD g_last_time;
static int g_last_choice = -1;
/* What a set-up remembers when its deck file could not be opened. */
#define GAME_DECK (-2)
static BOOL g_failure_logged;

static char folded(char character)
{
    if (character >= 'A' && character <= 'Z') return (char)(character + ('a' - 'A'));
    return character == '\\' ? '/' : character;
}

/* `prefix` is lower case with forward slashes, for example "data/y/file/". */
BOOL pocgp_decks_is_opponent_deck(const char* path, const char* prefix)
{
    static const char extension[] = ".ydc";
    size_t length, i;
    if (!path || !prefix) return FALSE;
    for (i = 0; prefix[i]; i++) if (folded(path[i]) != prefix[i]) return FALSE;
    length = strlen(path);
    if (length < i + sizeof(extension) || length >= MAX_PATH) return FALSE;
    /* Only a file directly in that folder. */
    for (; i < length; i++) if (folded(path[i]) == '/' || path[i] == ':') return FALSE;
    for (i = 0; i < sizeof(extension) - 1; i++)
        if (folded(path[length - (sizeof(extension) - 1) + i]) != extension[i]) return FALSE;
    return TRUE;
}

void pocgp_decks_bind(POCGP_OPEN open, const char* prefix, const POCGP_SETTINGS* settings)
{
    g_open = open;
    g_prefix = prefix;
    g_settings = settings;
    g_last_path[0] = 0;
    g_last_choice = -1;
    g_failure_logged = FALSE;
}

void* __cdecl pocgp_decks_open(const char* path, const char* mode)
{
    if (g_settings && g_settings->deck_count > 0 && pocgp_decks_is_opponent_deck(path, g_prefix))
    {
        DWORD now = GetTickCount();
        void* file = NULL;
        int choice = g_last_choice;
        if ((choice != GAME_DECK && (choice < 0 || choice >= g_settings->deck_count))
            || now - g_last_time > SAME_DUEL_MILLISECONDS || lstrcmpiA(path, g_last_path) != 0)
            choice = (int)(pocgp_random() % (unsigned)g_settings->deck_count);
        if (choice != GAME_DECK) file = g_open(g_settings->decks[choice], mode);
        if (!file && choice != GAME_DECK)
        {
            if (!g_failure_logged)
            {
                g_failure_logged = TRUE;
                pocgp_log("deck file %d could not be opened; the game's own deck is used", choice + 1);
            }
            choice = GAME_DECK;
        }
        lstrcpynA(g_last_path, path, sizeof(g_last_path));
        g_last_time = now;
        g_last_choice = choice;
        if (file)
        {
            pocgp_log("opponent deck %s: deck file %d of %d is used", path, choice + 1, g_settings->deck_count);
            return file;
        }
    }
    return g_open(path, mode);
}
