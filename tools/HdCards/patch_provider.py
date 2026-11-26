"""Apply the launcher display profile and the HD card integration to a pinned cnc-ddraw source tree.

The upstream tree is never stored in this repository. This script edits an extracted copy of the
pinned v7.1.0.0 archive in place, refusing any text that does not match the expected preimage.
"""
from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SOURCE_ARCHIVE_SHA256 = 'a4f638383364e3bc1e4682b1d549a1f5400fa0cc312ee216d1b42555b1c814eb'
PREIMAGES = {
    'src/dllmain.c': '47d7c0ad8fa032eaf73b6c8a3fe7d2cb4fbfacf42e11461a0bc266407c6d18d0',
    'src/utils.c': '9e6fdf08956964692a0e1e232b495ee1daae3a543afe786d24fe51fd3655f157',
    'src/dd.c': 'fcaab9e132b574f094b7948f43adaabb9ef4d1f0e7b69ff9881801cfb24c12ef',
    'src/ddsurface.c': 'd3070c13307429c7b337aa09b30c2148ed3a83f36a56f966d1e0a60bf5784696',
    'cnc-ddraw.vcxproj': 'c6dfbde83432117b2e1570ca17bdfafc80d67c00a47f95e7ef3e8b9c6aeefccb',
}
CR = chr(13)
LF = chr(10)
_CRLF: dict[Path, bool] = {}


class PatchError(Exception):
    pass


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path: Path) -> str:
    """Text is edited with LF newlines; the file's original convention is restored on write."""
    text = path.read_bytes().decode('utf-8')
    _CRLF[path] = CR + LF in text
    return text.replace(CR + LF, LF)


def write(path: Path, text: str) -> None:
    if _CRLF.get(path, True):
        text = text.replace(LF, CR + LF)
    path.write_bytes(text.encode('utf-8'))


def replace_once(text: str, old: str, new: str, what: str) -> str:
    if text.count(old) != 1:
        raise PatchError(f'{what}: expected exactly one match, found {text.count(old)}')
    return text.replace(old, new)


def cut(text: str, start_marker: str, end_marker: str, what: str, replacement: str = '') -> str:
    start = text.find(start_marker)
    end = text.find(end_marker, start + 1) if start >= 0 else -1
    if start < 0 or end <= start:
        raise PatchError(f'{what}: precondition mismatch')
    return text[:start] + replacement + text[end:]


def apply_display_profile(root: Path, maximize_source: Path) -> None:
    """Same edits as tools/DisplayComponent/build-profile.ps1 (Original aspect ratio profile)."""
    path = root / 'src/dllmain.c'
    text = read(path)
    text = replace_once(text, '        indeo_enable();',
                        '        /* Launcher profile: do not register legacy codecs. */', 'indeo enable')
    text = replace_once(text, '        indeo_disable();',
                        '        /* Launcher profile: never delete user codec registrations. */', 'indeo disable')
    text = cut(text, '        /* WINE does not support SetThreadExecutionState',
               '        /* Launcher profile: do not register legacy codecs. */', 'screensaver')
    text = cut(text, '        if (g_screensaver_disabled)', '        ULONG(WINAPI* remove_handler)', 'screensaver restore')
    write(path, text)

    path = root / 'src/utils.c'
    text = read(path)
    text = cut(text, 'void util_toggle_maximize()', 'void util_toggle_fullscreen()', 'maximize',
               read(maximize_source).rstrip() + LF + LF)
    write(path, text)

    path = root / 'src/dd.c'
    text = read(path)
    text = cut(text, '        if (g_config.singlecpu)',
               '        if (GetProcessAffinityMask(proc, &proc_affinity, &system_affinity))', 'affinity',
               '        /* Launcher profile: preserve the inherited process affinity. */' + LF + LF)
    write(path, text)

    git_header = root / 'inc/git.h'
    _CRLF[git_header] = True
    write(git_header, '#define GIT_COMMIT "541b5de-launcher-hd"' + LF + '#define GIT_BRANCH "launcher-hd-cards"' + LF)


def strip_build_events(text: str) -> str:
    text, events = re.subn(r'\s*<(PreBuildEvent|PostBuildEvent)>.*?</\1>', '', text, flags=re.S)
    if events < 1:
        raise PatchError('project: build events not found')
    return text


def apply_project(root: Path, sources: list[str], headers: list[str]) -> None:
    path = root / 'cnc-ddraw.vcxproj'
    text = strip_build_events(read(path))
    text = re.sub(r'<WholeProgramOptimization>[^<]*</WholeProgramOptimization>',
                  '<WholeProgramOptimization>false</WholeProgramOptimization>', text)
    # No debug records and a reproducible link, so the same sources and toolchain give the same file.
    text, compiled = re.subn(r'<Optimization>[^<]*</Optimization>',
                             lambda match: match.group(0) + '<DebugInformationFormat>None</DebugInformationFormat>', text)
    text, linked = re.subn(r'<Link>', '<Link><GenerateDebugInformation>false</GenerateDebugInformation>'
                           '<AdditionalOptions>/Brepro %(AdditionalOptions)</AdditionalOptions>', text)
    if compiled < 1 or linked < 1:
        raise PatchError('project: compile or link settings not found')
    anchor = '    <ClCompile Include="src\\blt.c" />' + LF
    text = replace_once(text, anchor,
                        ''.join(f'    <ClCompile Include="src\\{name}" />' + LF for name in sources) + anchor,
                        'project sources')
    anchor = '    <ClInclude Include="inc\\blt.h" />' + LF
    text = replace_once(text, anchor,
                        ''.join(f'    <ClInclude Include="inc\\{name}" />' + LF for name in headers) + anchor,
                        'project headers')
    write(path, text)


def apply_hd_integration(root: Path) -> None:
    """Small call-outs from the provider into the authored pochd_* translation units."""
    path = root / 'src/dd.c'
    text = read(path)
    # renderer=hd selects the authored renderer.
    text = replace_once(
        text,
        "        if (tolower(g_config.renderer[0]) == 'd') /* direct3d9 or direct3d9on12*/",
        "        if (tolower(g_config.renderer[0]) == 'h') /* launcher HD cards */" + LF
        + '        {' + LF
        + '            g_ddraw.renderer = pochd_render_main;' + LF
        + '        }' + LF
        + "        else if (tolower(g_config.renderer[0]) == 'd') /* direct3d9 or direct3d9on12*/",
        'renderer selection')
    text = replace_once(text, '#include "dd.h"', '#include "dd.h"' + LF + '#include "pochd.h"', 'dd.c include')
    # Power of Chaos restores the display before requesting DDSCL_NORMAL, without another
    # SetDisplayMode. The wrapper must restart HD even though the logical surface size remains set.
    coop_mode = """    if ((dwFlags & DDSCL_NORMAL) && !(dwFlags & DDSCL_FULLSCREEN))
    {
        if (g_config.fake_mode[0])"""
    text = replace_once(
        text, coop_mode,
        """    static HWND hd_window_from_game_menu;
    if (hwnd == g_ddraw.hwnd && g_ddraw.renderer == pochd_render_main && g_config.windowed &&
        hd_window_from_game_menu == hwnd && (dwFlags & DDSCL_EXCLUSIVE) && (dwFlags & DDSCL_FULLSCREEN))
    {
        hd_window_from_game_menu = NULL;
        g_config.fullscreen = TRUE;
    }

    if ((dwFlags & DDSCL_NORMAL) && !(dwFlags & DDSCL_FULLSCREEN))
    {
        if (hwnd == g_ddraw.hwnd && g_ddraw.renderer == pochd_render_main && g_config.windowed &&
            !g_config.fake_mode[0] && g_ddraw.width && g_ddraw.height && !g_ddraw.render.run &&
            !g_ddraw.render.thread)
        {
            hd_window_from_game_menu = hwnd;
            g_config.fullscreen = FALSE;
            return dd_SetDisplayMode(g_ddraw.width, g_ddraw.height, g_ddraw.bpp, 0);
        }
        if (g_config.fake_mode[0])""",
        'restart HD on native windowed transition')
    write(path, text)

    path = root / 'src/dllmain.c'
    text = read(path)
    text = replace_once(text, '#include "dllmain.h"', '#include "dllmain.h"' + LF + '#include "pochd.h"',
                        'dllmain include')
    text = replace_once(text, '        /* Launcher profile: do not register legacy codecs. */',
                        '        /* Launcher profile: do not register legacy codecs. */' + LF + '        pochd_attach();',
                        'attach call')
    write(path, text)

    # Surface events: the game presents by copying its canvas to the primary surface. The provider
    # reports these calls from inside its own entry points so the frame and the HD draw list stay paired.
    path = root / 'src/ddsurface.c'
    text = read(path)
    text = replace_once(text, '#include "ddsurface.h"', '#include "ddsurface.h"' + LF + '#include "pochd.h"',
                        'ddsurface include')
    events = {
        'dds_Blt': 'pochd_dd_event(POCHD_DD_BLT, This, lpDDSrcSurface, lpDestRect, lpSrcRect, dwFlags);',
        'dds_BltFast': 'pochd_dd_event(POCHD_DD_BLTFAST, This, lpDDSrcSurface, NULL, lpSrcRect, dwFlags);',
        'dds_Flip': 'pochd_dd_event(POCHD_DD_FLIP, This, lpDDSurfaceTargetOverride, NULL, NULL, dwFlags);',
        'dds_Lock': 'pochd_dd_event(POCHD_DD_LOCK, This, NULL, lpDestRect, NULL, dwFlags);',
        'dds_Unlock': 'pochd_dd_event(POCHD_DD_UNLOCK, This, NULL, lpRect, NULL, 0);',
        'dds_GetDC': 'pochd_dd_event(POCHD_DD_GETDC, This, NULL, NULL, NULL, 0);',
        'dds_ReleaseDC': 'pochd_dd_event(POCHD_DD_RELEASEDC, This, NULL, NULL, NULL, 0);',
    }
    for function, call in events.items():
        pattern = re.compile(r'(\nHRESULT ' + function + r'\([^)]*\)\n\{\n)')
        if len(pattern.findall(text)) != 1:
            raise PatchError(f'{function}: definition not found exactly once')
        text = pattern.sub(lambda match, call=call: match.group(1) + '    ' + call + LF, text)
    write(path, text)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--source-root', required=True, type=Path, help='extracted cnc-ddraw-7.1.0.0 directory')
    parser.add_argument('--maximize-source', type=Path,
                        default=HERE.parent / 'DisplayComponent' / 'launcher-maximize.c')
    parser.add_argument('--authored', type=Path, default=HERE / 'src', help='directory with pochd*.c and pochd*.h')
    parser.add_argument('--profile-only', action='store_true', help='apply only the display profile (baseline build)')
    args = parser.parse_args()
    root = args.source_root.resolve()
    sources = sorted(path.name for path in args.authored.glob('pochd*.c*'))
    headers = sorted(path.name for path in args.authored.glob('pochd*.h'))
    try:
        for relative, expected in PREIMAGES.items():
            actual = sha256(root / relative)
            if actual != expected:
                raise PatchError(f'unexpected preimage for {relative}: {actual}')
        apply_display_profile(root, args.maximize_source)
        if args.profile_only:
            path = root / 'cnc-ddraw.vcxproj'
            write(path, strip_build_events(read(path)))
            return 0
        if not sources:
            raise PatchError('no authored sources found')
        for name in sources:
            shutil.copyfile(args.authored / name, root / 'src' / name)
        for name in headers:
            shutil.copyfile(args.authored / name, root / 'inc' / name)
        apply_project(root, sources, headers)
        apply_hd_integration(root)
    except PatchError as error:
        print(f'patch_provider: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
