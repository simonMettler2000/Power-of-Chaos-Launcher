/* HD card artwork: facts about the games' image library that the tracker relies on. */
#ifndef POCHD_GAME_H
#define POCHD_GAME_H

/* Byte offsets inside the library's image object. */
typedef struct POCHD_LAYOUT
{
    int bits;        /* pointer to the top-left pixel */
    int pitch;       /* bytes per row */
    int left, top, right, bottom;   /* image bounds */
    int format;      /* pixel format tag */
    int format_565;  /* tag value for 16-bit RGB565 */
    int format_alpha;/* tag value for RGB565 with transparency: four bytes a pixel, colour word, then a 0..15 opacity byte */
} POCHD_LAYOUT;

extern POCHD_LAYOUT g_pochd_layout;

#endif
