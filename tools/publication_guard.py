"""Admit launcher-authored text source only; inspect a working tree or exact Git tree."""
from __future__ import annotations
import argparse
import re
import subprocess
import sys
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
TOP_LEVEL = {'.gitignore', '.gitattributes', '.editorconfig', 'AGENTS.md', 'README.md', 'Directory.Build.props',
             'global.json', 'NuGet.Config', 'PowerOfChaos.Launcher.sln'}
ALLOWED = {'.cs', '.axaml', '.csproj', '.manifest', '.svg', '.md', '.py', '.yml', '.yaml', '.json'}
# These build/probe tools are authored here. This does not admit native code,
# downloaded sources or arbitrary shell scripts elsewhere in the repository.
AUTHORED_NATIVE_TOOLS = {
    'tools/HdCardsComponent/Build-Native.ps1',
    'tools/HdCardsComponent/Build-Provider.ps1',
    'tools/HdCardsComponent/ConfigAdversarialProbe.cpp',
    'tools/HdCardsComponent/hd_visible.cpp',
    'tools/HdCardsComponent/hd_visible.def',
    'tools/HdCardsComponent/hd_visible.h',
    'tools/HdCardsComponent/hd_visible_d3d9.cpp',
    'tools/HdCardsComponent/hd_visible_test.cpp',
    'tools/HdCardsComponent/hd_visible_test_observer.cpp',
    'tools/HdCardsComponent/hd_visible_test_observer.h',
    'tools/HdCardsComponent/local-paths.ps1',
    'tools/HdCardsComponent/setup-component.ps1',
    'tools/HdCardsComponent/SyntheticDisplayProbe.cpp',
    'tools/HdCardsComponent/SyntheticProviderProbe.cpp',
    'tools/HdCardsComponent/verify-tools.ps1',
    'tools/DisplayComponent/build-profile.ps1',
    'tools/DisplayComponent/launcher-maximize.c',
    'tools/DisplayComponent/setup-component.ps1',
    'tools/DisplayComponent/verify-tools.ps1',
    'tools/DisplayProbe/DisplayProbe.cpp',
    'tools/DisplayProbe/ComponentIdentity.cpp',
    'tools/DisplayProbe/Build-DisplayProbe.ps1',
    'tools/DisplayProbe/Run-DisplayProbe.ps1',
}
FORBIDDEN_PARTS = {'bin', 'obj', '.local', 'artifacts', 'packages', 'games', 'game-files', 'saves', 'mod-packs', '__pycache__'}
RUNTIME_NAMES = {'launcher-settings.json', 'playtime.json', 'sessions.json', 'appsettings.local.json'}
MAX_BYTES = 160_000

def git(*args):
    return subprocess.run(['git', *args], cwd=ROOT, check=True, capture_output=True).stdout

def check(path: str, content: bytes):
    errors = []
    p = PurePosixPath(path)
    if p.is_absolute() or '..' in p.parts or any(x in FORBIDDEN_PARTS for x in p.parts):
        errors.append('unexpected or generated path')
    admitted = path in TOP_LEVEL or path in AUTHORED_NATIVE_TOOLS or (
        p.parts[0] in {'src', 'tests', 'docs', 'tools'} and p.suffix.lower() in ALLOWED) or (
        path.startswith('.github/workflows/') and p.suffix in {'.yml', '.yaml'})
    if not admitted or p.name in RUNTIME_NAMES: errors.append('not admitted launcher source')
    if len(content) > MAX_BYTES: errors.append('source file exceeds inspection limit')
    if content.startswith((b'MZ', b'PK\x03\x04', b'KCEJYUGI', b'RIFF', b'\x89PNG', b'BM', b'\xff\xd8\xff')):
        errors.append('binary/game/archive signature')
    if b'\0' in content: errors.append('binary NUL bytes')
    try: text = content.decode('utf-8-sig')
    except UnicodeDecodeError: return errors + ['not UTF-8 text']
    if re.search(r'[A-Za-z0-9+/]{800,}={0,2}', text): errors.append('large encoded blob')
    if re.search(r'(?i)\b(?:gh[pousr]_[A-Za-z0-9]{25,}|github_pat_[A-Za-z0-9_]{25,})', text):
        errors.append('possible credential')
    if re.search(r'(?i)(?:C:[\\/]+Users[\\/]+|/Users/|/home/)[A-Za-z0-9_.-]+', text):
        errors.append('personal absolute path')
    if p.suffix == '.svg' and re.search(r'(?i)<script|(?:href|src)\s*=\s*["\'](?:https?:|data:|file:)', text):
        errors.append('SVG includes external or embedded content')
    return errors

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--working-tree', action='store_true')
    parser.add_argument('--commit', help='inspect every reachable tree before publishing this revision')
    args = parser.parse_args()
    if bool(args.working_tree) == bool(args.commit): parser.error('choose --working-tree or --commit')
    actual_root = Path(git('rev-parse', '--show-toplevel').decode().strip()).resolve()
    if actual_root != ROOT:
        parser.error('the launcher must be initialized as its own Git repository')
    failures = []
    inspected = set()
    if args.working_tree:
        paths = git('ls-files', '-z', '--cached', '--others', '--exclude-standard').split(b'\0')
        for raw in paths:
            if not raw: continue
            path = raw.decode('utf-8')
            if path in inspected: continue
            inspected.add(path)
            actual = ROOT / path
            errors = ['symbolic link is not admitted'] if actual.is_symlink() else check(path, actual.read_bytes())
            if errors: failures.append((path, errors))
    else:
        revision_sha = git('rev-parse', '--verify', args.commit + '^{commit}').decode().strip()
        if not re.fullmatch(r'[0-9a-f]{40,64}', revision_sha): parser.error('invalid commit')
        # Scan history too: removing an accidentally committed binary is not enough.
        for revision in git('rev-list', revision_sha).decode().splitlines():
            for row in git('ls-tree', '-r', '-z', revision).split(b'\0'):
                if not row: continue
                metadata, raw_path = row.split(b'\t', 1)
                mode, kind, blob = metadata.decode().split()
                path = raw_path.decode('utf-8')
                key = (path, blob)
                if key in inspected: continue
                inspected.add(key)
                errors = ['non-regular source entry'] if mode not in {'100644', '100755'} or kind != 'blob' else check(path, git('cat-file', 'blob', blob))
                if errors: failures.append((f'{revision[:12]}:{path}', errors))
    for path, errors in failures: print(path + ': ' + '; '.join(errors))
    if len(inspected) < 15:
        failures.append(('repository', ['expected launcher source entries are missing']))
        print('repository: expected launcher source entries are missing')
    print(f'Publication guard: {len(inspected)} source entries inspected; {len(failures)} rejected.')
    return bool(failures)

if __name__ == '__main__': sys.exit(main())
