"""Local-only source patch. Never writes outside a fresh owned destination.
No original executable/provider is loaded or run by this tool.
"""
from pathlib import Path
import argparse
import hashlib
import shutil
import json
import xml.etree.ElementTree as ET
import sys
sys.dont_write_bytecode = True
from local_paths import require_private, no_links, no_tree_links
from verify_source import verify_tree


def once(text, before, after):
    if text.count(before) != 1:
        raise ValueError(f"Patch precondition not unique: {before[:100]!r}")
    return text.replace(before, after, 1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--synthetic", action="store_true", help="Add explicit readback observer only to authored synthetic test build")
    args = parser.parse_args()
    own = Path(__file__).resolve().parent
    destination = require_private(args.destination, fresh=True)
    source = require_private(args.source)
    no_tree_links(source)
    verify_tree(source, "aspect")
    for name in ("hd_visible.h", "hd_visible.cpp", "hd_visible_d3d9.cpp", "hd_visible_gdi.h", "hd_visible_gdi.cpp", "hd_visible_png.h", "hd_visible_png.cpp", "hd_visible_test_observer.h", "hd_visible_test_observer.cpp"):
        no_links(own / name)
    pinned = {"src/render_d3d9.c": "2b1948b1ad1a8c0fd0b07aee372a2a4a4f37b4944e3544213181e984d65737f8",
              "cnc-ddraw.vcxproj": "cbdf3e15734bb8cc2f9f173e490999d1821de2bbd698266b0012a3ba065d984d",
              "src/render_gdi.c": "378c1a658e7cf0869e2f1daaa9aec8412f682ec67cd053d93ddb853c9389f6e6"}
    original = {name: hashlib.sha256((source / name).read_bytes()).hexdigest() for name in pinned}
    if original != pinned:
        raise ValueError("Source pins do not match inspected source")
    shutil.copytree(source, destination)
    for name in ("hd_visible.h", "hd_visible.cpp", "hd_visible_d3d9.cpp", "hd_visible_gdi.h", "hd_visible_gdi.cpp", "hd_visible_png.h", "hd_visible_png.cpp"):
        shutil.copy2(own / name, destination / "src" / name)
    path = destination / "src/render_d3d9.c"
    text = path.read_text()
    text = once(text, '#include "config.h"', '#include "config.h"\n#include "hd_visible.h"')
    text = once(text, '    Sleep(250);', '''    wchar_t hdv_config_path[32768];
    DWORD hdv_config_length = GetEnvironmentVariableW(L"POC_HD_CONFIG", hdv_config_path, 32768);
    if (hdv_config_length > 0 && hdv_config_length < 32768)
        hdv_provider_configure(hdv_config_path);
    Sleep(250);''')
    text = once(text, '        fpsl_frame_start();', '        fpsl_frame_start();\n        BOOL hdv_frame_eligible = FALSE;')
    text = once(text, '            D3DLOCKED_RECT lock_rc;', '''            hdv_frame_eligible = g_ddraw.bpp == 16 && g_ddraw.width == 800 && g_ddraw.height == 600;
            D3DLOCKED_RECT lock_rc;''')
    text = once(text, '                RECT rc = { 0, 0, g_ddraw.width, g_ddraw.height };',
        '                hdv_provider_invalidate();\n                RECT rc = { 0, 0, g_ddraw.width, g_ddraw.height };')
    text = once(text, '                    IDirect3DTexture9_UnlockRect(g_d3d9.surface_tex[tex_index], 0);', '''                    /* Match a private copy of this exact upload, never primary memory.
                       Nothing writes lock_rc between capture and UnlockRect. */
                    if (hdv_frame_eligible && hdv_provider_bind_device(g_d3d9.device))
                        hdv_provider_capture(lock_rc.pBits, 800, 600, lock_rc.Pitch, g_config.rgb555 ? HDV_RGB555 : HDV_RGB565);
                    if (FAILED(IDirect3DTexture9_UnlockRect(g_d3d9.surface_tex[tex_index], 0)))
                        hdv_provider_invalidate();''')
    text = once(text, '        IDirect3DDevice9_EndScene(g_d3d9.device);', '''        hdv_provider_draw(g_d3d9.device,
            g_ddraw.render.viewport.x, g_ddraw.render.viewport.y,
            g_ddraw.render.viewport.width, g_ddraw.render.viewport.height,
            hdv_frame_eligible && !g_config.vhack && !g_ddraw.child_window_exists && !g_ddraw.bnet_active && !needs_update);
        IDirect3DDevice9_EndScene(g_d3d9.device);''')
    if args.synthetic:
        for name in ("hd_visible_test_observer.h", "hd_visible_test_observer.cpp"):
            shutil.copy2(own / name, destination / "src" / name)
        text = once(text, '#include "hd_visible.h"', '#include "hd_visible.h"\n#include "hd_visible_test_observer.h"')
        text = once(text, '        hdv_provider_draw(g_d3d9.device,', '        int hdv_replaced = hdv_provider_draw(g_d3d9.device,')
        text = once(text, '        IDirect3DDevice9_EndScene(g_d3d9.device);',
                    '        IDirect3DDevice9_EndScene(g_d3d9.device);\n        hdv_test_observe(g_d3d9.device, hdv_replaced);')
        exports = destination / "exports.def"
        exports.write_text(exports.read_text() + '\n    hdv_test_read\n    hdv_test_gdi_control\n    hdv_test_gdi_read_state\n')
    text = once(text, '            DWORD_PTR result;', '            hdv_provider_invalidate();\n            DWORD_PTR result;')
    text = once(text, '    return 0;\n}', '    hdv_provider_shutdown();\n    return 0;\n}')
    path.write_text(text)
    # Explicit reset invalidation; no cached GPU resources survive reset.
    text = path.read_text()
    text = once(text, 'BOOL d3d9_reset(BOOL windowed)\n{', 'BOOL d3d9_reset(BOOL windowed)\n{\n    hdv_provider_invalidate();')
    path.write_text(text)
    # Exact independently inspected upstream GDI file; normal renderer base is
    # replaced only when the separate backend owns a complete eligible frame.
    gdi = destination / "src/render_gdi.c"
    gtext = gdi.read_text()
    gtext = once(gtext, '#include "config.h"', '#include "config.h"\n#include "hd_visible_gdi.h"')
    gtext = once(gtext, '    Sleep(500);', '    HdvGdi* hdvg = hdvg_create();\n    wchar_t hdvg_path[32768];\n    DWORD hdvg_path_length = GetEnvironmentVariableW(L"POC_HD_CONFIG", hdvg_path, 32768);\n    if (hdvg && hdvg_path_length > 0 && hdvg_path_length < 32768)\n        hdvg_configure(hdvg, hdvg_path);\n    Sleep(500);')
    gtext = once(gtext, '        fpsl_frame_start();', '        fpsl_frame_start();\n        int hdvg_result = 0;')
    gtext = once(gtext, '    Sleep(500);', '    BOOL hdvg_previous_child = FALSE;\n    Sleep(500);')
    gtext = once(gtext, '            if (InterlockedExchange(&g_ddraw.render.clear_screen, FALSE) || g_ddraw.child_window_exists)', '''            /* A child frame uses the original native-size base fallback.
               Clear its pixels outside the scaled viewport on dialog exit. */
            if (hdvg_previous_child && !g_ddraw.child_window_exists)
                InterlockedExchange(&g_ddraw.render.clear_screen, TRUE);
            hdvg_previous_child = g_ddraw.child_window_exists;
            if (InterlockedExchange(&g_ddraw.render.clear_screen, FALSE) || g_ddraw.child_window_exists)''')
    first = gtext.index('            int lines_copied = 0;')
    last = gtext.index('            if (g_config.lock_surfaces)\n                LeaveCriticalSection', first)
    original_base = gtext[first:last]
    eligibility = '''            /* Exact supported RGB565 top-down normal full-frame contract.
               hdvg_present copies once and displays/matches that owned copy.
               Return zero requires this original base block to repaint. */
            if (hdvg && !warning_end_tick && !g_config.vhack &&
                !g_ddraw.bnet_active && !g_ddraw.child_window_exists &&
                !g_config.rgb555 && g_ddraw.bpp == 16 &&
                g_ddraw.width == 800 && g_ddraw.height == 600 &&
                g_ddraw.primary->pitch == 1600 && g_ddraw.primary->bytes_pp == 2 &&
                g_ddraw.primary->surface && g_ddraw.primary->bmi &&
                g_ddraw.primary->bmi->bmiHeader.biSize == sizeof(BITMAPINFOHEADER) &&
                g_ddraw.primary->bmi->bmiHeader.biWidth == 800 &&
                g_ddraw.primary->bmi->bmiHeader.biHeight == -600 &&
                g_ddraw.primary->bmi->bmiHeader.biPlanes == 1 &&
                g_ddraw.primary->bmi->bmiHeader.biBitCount == 16 &&
                g_ddraw.primary->bmi->bmiHeader.biCompression == BI_BITFIELDS &&
                ((DWORD*)g_ddraw.primary->bmi->bmiColors)[0] == 0xf800 &&
                ((DWORD*)g_ddraw.primary->bmi->bmiColors)[1] == 0x07e0 &&
                ((DWORD*)g_ddraw.primary->bmi->bmiColors)[2] == 0x001f)
                hdvg_result = hdvg_present(hdvg, g_ddraw.render.hdc,
                    g_ddraw.render.viewport.x, g_ddraw.render.viewport.y,
                    g_ddraw.render.viewport.width, g_ddraw.render.viewport.height,
                    g_ddraw.primary->surface, 800, 600, 1600, 1, TRUE);
            if (!hdvg_result)
            {
'''
    if args.synthetic:
        eligibility = ('            BOOL hdvg_test_saved_bnet = g_ddraw.bnet_active;\n'
            '            if (hdv_test_gdi_controls() & HDV_TEST_GDI_FORCE_BNET)\n'
            '                g_ddraw.bnet_active = TRUE;\n' + eligibility)
    gtext = gtext[:first] + eligibility + ''.join('    '+line if line.strip() else line for line in original_base.splitlines(True)) + '            }\n\n' + gtext[last:]
    if args.synthetic:
        gtext = once(gtext, '#include "hd_visible_gdi.h"', '#include "hd_visible_gdi.h"\n#include "hd_visible_test_observer.h"')
        gtext = once(gtext, '    HdvGdi* hdvg = hdvg_create();', '    uint64_t hdvg_test_instance = hdv_test_gdi_begin();\n    HdvGdi* hdvg = hdvg_create();')
        gtext = once(gtext, '            if (g_config.lock_surfaces)\n                LeaveCriticalSection(&g_ddraw.primary->cs);', '''            uint32_t hdvg_test_flags =
                (g_config.vhack ? HDV_TEST_GDI_VHACK : 0) |
                (g_ddraw.child_window_exists ? HDV_TEST_GDI_CHILD : 0) |
                (g_ddraw.bnet_active ? HDV_TEST_GDI_BNET : 0);
            hdv_test_observe_gdi_frame(g_ddraw.render.hdc, g_ddraw.render.width, g_ddraw.render.height,
                hdvg_result, hdvg_test_instance, g_ddraw.render.viewport.x, g_ddraw.render.viewport.y,
                g_ddraw.render.viewport.width, g_ddraw.render.viewport.height, hdvg_test_flags);
            g_ddraw.bnet_active = hdvg_test_saved_bnet;
            if (g_config.lock_surfaces)
                LeaveCriticalSection(&g_ddraw.primary->cs);''')
    gtext = once(gtext, '    return TRUE;', '    hdvg_shutdown(hdvg);\n    hdvg_destroy(hdvg);\n    return TRUE;')
    if args.synthetic:
        gtext = once(gtext, '    return TRUE;', '    hdv_test_gdi_end(hdvg_test_instance);\n    return TRUE;')
    gdi.write_text(gtext)
    project = destination / "cnc-ddraw.vcxproj"
    ns = "http://schemas.microsoft.com/developer/msbuild/2003"
    ET.register_namespace("", ns)
    tree = ET.parse(project)
    group = ET.SubElement(tree.getroot(), f"{{{ns}}}ItemGroup")
    source_names = ["hd_visible.cpp", "hd_visible_d3d9.cpp", "hd_visible_gdi.cpp", "hd_visible_png.cpp"]
    if args.synthetic:
        source_names.append("hd_visible_test_observer.cpp")
    for name in source_names:
        item = ET.SubElement(group, f"{{{ns}}}ClCompile", Include="src\\" + name)
        ET.SubElement(item, f"{{{ns}}}LanguageStandard").text = "stdcpp17"
        ET.SubElement(item, f"{{{ns}}}ExceptionHandling").text = "Sync"
        if name == "hd_visible_png.cpp":
            ET.SubElement(item, f"{{{ns}}}AdditionalIncludeDirectories").text = "$(ProjectDir)src;$(ProjectDir)inc;%(AdditionalIncludeDirectories)"
    for link in tree.findall(f".//{{{ns}}}ItemDefinitionGroup/{{{ns}}}Link"):
        deps = link.find(f"{{{ns}}}AdditionalDependencies")
        if deps is None:
            deps = ET.SubElement(link, f"{{{ns}}}AdditionalDependencies")
        deps.text = "bcrypt.lib;windowscodecs.lib;ole32.lib;" + (deps.text or "%(AdditionalDependencies)")
    tree.write(project, encoding="utf-8", xml_declaration=True)
    receipt = {"source": str(source), "destination": str(destination), "original": original,
               "authored": {name: hashlib.sha256((own/name).read_bytes()).hexdigest()
                            for name in ("hd_visible.h", "hd_visible.cpp", "hd_visible_d3d9.cpp", "hd_visible_gdi.h", "hd_visible_gdi.cpp", "hd_visible_png.h", "hd_visible_png.cpp", "patch_provider.py")},
               "loaded_or_executed": False}
    receipt["synthetic_observer"] = args.synthetic
    (destination.parent / (destination.name + "-patch-receipt.json")).write_text(json.dumps(receipt, indent=2))
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    main()
