"""Canonical authored CPU/memory-DC regression tests; no provider/window/game.

Builds only after exact source, external provider tree and complete toolchain
pins pass. All fixtures are generated arithmetic patterns in fresh ignored
storage. Actual artwork lists/assets and synthetic provider windows are outside
this entry point.
"""
from pathlib import Path
from datetime import datetime, timezone
import argparse, hashlib, json, subprocess, sys, zlib
sys.dont_write_bytecode = True
from local_paths import require_private, no_tree_links
from verify_source import verify_tree
from native_fixtures import generate_all


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8', newline='\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--external-source', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    own = Path(__file__).resolve().parent
    output = require_private(args.output, fresh=True)
    external = require_private(args.external_source)
    no_tree_links(own)
    no_tree_links(external)
    verify_tree(external, 'aspect')
    pins = json.loads((own / 'component-pins.json').read_text(encoding='utf-8-sig'))
    native = {p.name: sha(p) for p in own.iterdir() if p.suffix in ('.cpp', '.h', '.def')}
    if native != pins['nativeSources']:
        raise ValueError('Exact native source pins/inventory mismatch')
    vendor_pins = {'src/lodepng.c': 'a7f82599ae6a4dbf0f3db3aa45a6125a1e88ba4d84c1f4be670bf3b4b5a27c86',
                   'inc/lodepng.h': '52a3e979fdc3048c804eec72a7927c3095ac254b03580f35539d46b72ff75f52'}
    for name, expected in vendor_pins.items():
        if sha(external / name) != expected:
            raise ValueError('Pinned external decoder mismatch')
    # Every authored recipe/header/helper/test and verifier is a build input.
    source_inputs = {str(p): sha(p) for p in own.iterdir()
                     if p.is_file() and p.suffix in ('.cpp', '.h', '.def', '.ps1', '.py', '.json')}
    source_inputs.update({str(external / name): sha(external / name) for name in vendor_pins})
    output.mkdir(parents=True)
    commands = []

    def run(label, argv, timeout=240):
        start = datetime.now(timezone.utc).isoformat()
        log = output / (label + '.log')
        with log.open('xb') as stream:
            result = subprocess.run([str(x) for x in argv], cwd=output, stdout=stream,
                                    stderr=subprocess.STDOUT, timeout=timeout)
        commands.append(dict(label=label, argv=[str(x) for x in argv], exit=result.returncode,
                             start=start, finish=datetime.now(timezone.utc).isoformat(),
                             log=log.name, sha256=sha(log)))
        write(output / 'commands.json', commands)
        if result.returncode:
            raise RuntimeError(label + ' failed; see ' + str(log))

    run('toolchain', [sys.executable, '-B', own / 'verify-toolchain.py'])
    write(output / 'source-inputs.json', source_inputs)
    fixture_rows = generate_all(output / 'fixtures')
    write(output / 'fixture-inputs.json', fixture_rows)
    build = output / 'build'
    run('native-build', ['pwsh', '-NoProfile', '-File', own / 'Build-Native.ps1',
                         '-OutputDirectory', build, '-ExternalSourceDirectory', external], timeout=600)
    suites = [('cpu', 'hd_visible_test', []),
              ('gdi-faults', 'hd_visible_gdi_fault_test', [output / 'fixtures/gdi-fault']),
              ('gdi-normal', 'hd_visible_gdi_test', [output / 'fixtures/gdi']),
              ('allocator', 'allocator_test', [output / 'fixtures/allocator']),
              ('png-regression', 'png_regression_test', [output / 'fixtures/regression']),
              ('master-profile', 'master_profile_test', [output / 'fixtures/profile/profile-manifest.tsv']),
              ('profile-gdi', 'profile_gdi_test', [output / 'fixtures/profile']),
              ('png-decode', 'png_decode_test', [output / 'fixtures/decode']),
              ('cursor', 'cursor_test', [output / 'fixtures/cursor'])]
    for label, executable, parameters in suites:
        run(label, [build / (executable + '.exe'), *parameters])
    if source_inputs != {name: sha(Path(name)) for name in source_inputs}:
        raise RuntimeError('Canonical inputs changed during verification')
    run('final-toolchain', [sys.executable, '-B', own / 'verify-toolchain.py'])
    write(output / 'receipt.json', dict(success=True, scope='authored CPU and memory-DC only',
        compiler_concurrency=1, inputs=source_inputs, inputs_unchanged=True, commands=commands,
        python=dict(path=sys.executable, sha256=sha(Path(sys.executable)),
                    zlib_runtime=zlib.ZLIB_RUNTIME_VERSION),
        generated_fixtures=fixture_rows,
        outputs={p.name: sha(p) for p in build.iterdir() if p.suffix in ('.exe', '.dll', '.obj')},
        actual_master_assets_or_lists_used=False, provider_or_window_or_game_executed=False))
    print('PASS: complete canonical authored CPU/GDI/PNG allocation/profile regressions')


if __name__ == '__main__':
    main()
