"""Fresh authored HD tests and existing launcher regression tests; never starts a game."""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def source_inventory():
    result = subprocess.run(['git', '-C', str(ROOT), 'ls-files', '-z', '--cached', '--others', '--exclude-standard'],
                            capture_output=True, check=True).stdout
    items = []
    for name in sorted(set(result.decode('utf-8').split('\0')) - {''}):
        path = ROOT / name
        if path.is_symlink() or not path.resolve().is_relative_to(ROOT):
            raise RuntimeError('Source link escapes checkout')
        if path.is_file():
            items.append(dict(path=name, sha256=sha(path), bytes=path.stat().st_size))
    return items

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path, help='A fresh private output directory; must not exist')
    args = parser.parse_args()
    output = args.output.absolute()
    for path in (output, *output.parents):
        if path.is_symlink():
            parser.error('Output directory links are unsupported')
    output.mkdir(parents=True, exist_ok=False)
    dotnet = shutil.which('dotnet')
    if dotnet is None:
        parser.error('dotnet SDK is required')
    environment = os.environ.copy()
    environment.update(DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
                       DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true')
    before = source_inventory()
    (output / 'inputs-before.json').write_text(json.dumps(before, indent=2) + '\n')
    commands = []

    def run(label, argv):
        start = datetime.now(timezone.utc).isoformat()
        log = output / (label + '.log')
        with log.open('xb') as stream:
            result = subprocess.run(argv, cwd=ROOT, env=environment, stdout=stream, stderr=subprocess.STDOUT)
        commands.append(dict(label=label, argv=argv, startUtc=start,
                             finishUtc=datetime.now(timezone.utc).isoformat(), exit=result.returncode,
                             log=log.name, sha256=sha(log)))
        (output / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        if result.returncode:
            raise RuntimeError(label + ' failed; inspect ' + str(log))

    run('sdk-version', [dotnet, '--version'])
    run('importer-tests', [sys.executable, '-I', '-B', str(ROOT / 'tests/test_hd_cards_import.py')])
    run('publication-working-tree', [sys.executable, '-I', '-B', str(ROOT / 'tools/publication_guard.py'), '--working-tree'])
    projects = ['PowerOfChaos.HdCards.Tests', 'PowerOfChaos.HdRendering.Tests']
    for configuration in ('Release', 'Debug'):
        artifacts = output / 'artifacts' / configuration
        run(configuration + '-build-probe', [dotnet, 'build', str(ROOT / 'tools/PowerOfChaos.HdCardProbe/PowerOfChaos.HdCardProbe.csproj'), '-c', configuration,
            '--no-incremental', '--artifacts-path', str(artifacts), '-p:UseSharedCompilation=false', '-m:1', '-nr:false'])
        for project in projects:
            project_file = ROOT / 'tests' / project / (project + '.csproj')
            run(configuration + '-build-' + project, [dotnet, 'build', str(project_file), '-c', configuration,
                '--no-incremental', '--artifacts-path', str(artifacts), '-p:UseSharedCompilation=false', '-m:1', '-nr:false'])
            candidates = [path for path in (artifacts / 'bin' / project).rglob(project + '.dll')
                          if 'ref' not in path.parts and 'refint' not in path.parts]
            if len(candidates) != 1:
                raise RuntimeError('Expected one fresh test image for ' + project)
            run(configuration + '-test-' + project, [dotnet, str(candidates[0])])
        # This existing harness discovers its source project by walking up from its image.
        # Keep its documented output layout; --no-incremental still rebuilds the exact sources.
        launcher = ROOT / 'tests/PowerOfChaos.Launcher.Tests/PowerOfChaos.Launcher.Tests.csproj'
        run(configuration + '-build-launcher-baseline', [dotnet, 'build', str(launcher), '-c', configuration,
            '--no-incremental', '-p:UseSharedCompilation=false', '-m:1', '-nr:false'])
        launcher_image = launcher.parent / 'bin' / configuration / 'net8.0/PowerOfChaos.Launcher.Tests.dll'
        run(configuration + '-test-launcher-baseline', [dotnet, str(launcher_image)])
    after = source_inventory()
    if before != after:
        raise RuntimeError('Source inputs changed during verification')
    receipt = dict(success=True, sourceInputs=before, commands=commands,
                   dotnet=dict(path=dotnet, sha256=sha(Path(dotnet))),
                   launcherFixtureRootOverride=environment.get('POC_LAUNCHER_TEST_ROOT'),
                   scope='Authored HD codec/pack/composition and existing launcher tests only; no game/native-HD acceptance')
    (output / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(dict(success=True, commands=len(commands), inputs=len(before), receipt=str(output / 'receipt.json'))))

if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
