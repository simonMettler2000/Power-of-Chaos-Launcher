/* Gameplay component: the settings file the launcher writes beside the component (pocgp.ini, UTF-8).

   new_card_rewards=1     every reward card is one the player does not own yet, while such cards remain
   opponent_deck=<file>   a deck file the opponent uses instead of a stock deck; may be given several
                          times, one is taken at random for each duel. A plain file name is looked up
                          beside the component.
   debug=<n>              more lines in pocgp.log */
#include "pocgp.h"

static int number(const char* text)
{
    int value = 0, digits = 0;
    while (*text >= '0' && *text <= '9' && digits++ < 6) value = value * 10 + (*text++ - '0');
    return value;
}

/* Turns a UTF-8 file name or path into the ANSI path the game's file routine can open.
   A name the system code page cannot hold exactly is refused rather than approximated. */
static BOOL deck_path(const char* value, const wchar_t* directory, char* target)
{
    wchar_t wide[MAX_PATH], full[MAX_PATH];
    BOOL approximated = FALSE;
    int length;
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, wide, MAX_PATH)) return FALSE;
    if (wide[0] == 0) return FALSE;
    if (wcschr(wide, L':') || wide[0] == L'\\' || wide[0] == L'/')
    {
        if (wcslen(wide) >= MAX_PATH) return FALSE;
        wcscpy(full, wide);
    }
    else
    {
        if (wcschr(wide, L'\\') || wcschr(wide, L'/') || wcslen(directory) + wcslen(wide) >= MAX_PATH) return FALSE;
        wcscpy(full, directory);
        wcscat(full, wide);
    }
    length = WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, full, -1, target, MAX_PATH, NULL, &approximated);
    return length > 0 && !approximated;
}

void pocgp_parse_settings(const char* text, size_t length, const wchar_t* directory, POCGP_SETTINGS* settings)
{
    size_t position = 0;
    memset(settings, 0, sizeof(*settings));
    if (length >= 3 && (BYTE)text[0] == 0xEF && (BYTE)text[1] == 0xBB && (BYTE)text[2] == 0xBF) position = 3;
    while (position < length)
    {
        char line[1024];
        size_t count = 0;
        char* value;
        while (position < length && text[position] != '\n')
        {
            if (count < sizeof(line) - 1) line[count++] = text[position];
            position++;
        }
        position++;
        while (count && (line[count - 1] == '\r' || line[count - 1] == ' ' || line[count - 1] == '\t')) count--;
        line[count] = 0;
        if (line[0] == ';' || line[0] == '#') continue;
        value = strchr(line, '=');
        if (!value) continue;
        *value++ = 0;
        if (strcmp(line, "new_card_rewards") == 0) settings->new_card_rewards = number(value) != 0;
        else if (strcmp(line, "debug") == 0) settings->debug = number(value);
        else if (strcmp(line, "opponent_deck") == 0 && settings->deck_count < POCGP_MAX_DECKS)
        {
            if (deck_path(value, directory, settings->decks[settings->deck_count])) settings->deck_count++;
        }
    }
}
