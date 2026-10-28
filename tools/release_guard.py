"""Independently inspect a Windows download; game/private payloads never belong in Release assets."""
from __future__ import annotations
import argparse,hashlib,json,re,stat,struct,sys,zipfile
from pathlib import Path,PurePosixPath

ROOT=Path(__file__).resolve().parents[1]
def unique_object(pairs):
    result={}
    for key,value in pairs:
        if key in result:raise ValueError('Duplicate metadata key')
        result[key]=value
    return result
def load(data):return json.loads(data.decode('utf-8-sig'),object_pairs_hook=unique_object)
def safe_name(name):
    p=PurePosixPath(name)
    if not name or len(name)>240 or p.is_absolute() or '\\' in name or ':' in name or re.search(r'[\x00-\x1f\x7f]',name):raise ValueError('Unsafe ZIP path')
    for part in name.split('/'):
        if part in ('','.','..') or part.endswith((' ','.')) or re.search(r'[<>"|?*]',part) or re.match(r'(?i)^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)',part):raise ValueError('Unsafe Windows filename')
    if any(part.lower() in {'.git','.local','obj','bin','saves','save','mod-cache','test-fixtures','launcher-data'} for part in p.parts):raise ValueError('Development/personal directory in download')
    if p.name.lower() in {'yugi_pc.exe','kaiba_pc.exe','joey_pc.exe','data.dat','voice.dat','system.dat','launcher-settings.json','playtime.json','sessions.json','appsettings.local.json'}:raise ValueError('Game or personal data in download')
def machine(data):
    if len(data)<0x40 or data[:2]!=b'MZ':raise ValueError('Expected PE image')
    offset=struct.unpack_from('<I',data,0x3c)[0]
    if offset+24>len(data) or data[offset:offset+4]!=b'PE\0\0':raise ValueError('Invalid PE header')
    return struct.unpack_from('<H',data,offset+4)[0]
def inspect(path,expected_commit=None):
    settings_bytes=(ROOT/'tools/Release/release-settings.json').read_bytes();settings=load(settings_bytes)
    component_bytes=(ROOT/'tools/DisplayComponent/component-pins.json').read_bytes();components=load(component_bytes)
    with zipfile.ZipFile(path) as archive:
        entries=archive.infolist();total=0;names={};content={}
        if not 20<=len(entries)<=5000:raise ValueError('Package entry count out of bounds')
        for entry in entries:
            safe_name(entry.filename)
            mode=entry.external_attr>>16
            if stat.S_IFMT(mode) not in (0,stat.S_IFREG) or entry.flag_bits&1 or entry.compress_type not in (zipfile.ZIP_DEFLATED,zipfile.ZIP_STORED):raise ValueError('Unsupported ZIP entry type')
            key=entry.filename.casefold()
            if key in names:raise ValueError('Duplicate/case-colliding ZIP entry')
            names[key]=entry.filename;total+=entry.file_size
            if entry.file_size>134217728 or total>1073741824:raise ValueError('Package expansion bound exceeded')
            with archive.open(entry) as stream:
                data=stream.read(entry.file_size+1)
            if len(data)!=entry.file_size:raise ValueError('ZIP size does not match actual content')
            content[entry.filename]=data
        manifest=load(content['package-manifest.json'])
        if manifest['schemaVersion']!=1 or manifest['selfContained'] is not True:raise ValueError('Not a self-contained package manifest')
        if manifest['runtimeIdentifier'] not in settings['supportedRuntimeIdentifiers'] or manifest['runtimeVersion']!=settings['runtimeVersion'] or manifest['targetFramework']!=settings['targetFramework']:raise ValueError('Package runtime differs from reviewed pins')
        if not re.fullmatch(r'[a-f0-9]{40}',manifest['sourceCommit']) or expected_commit and manifest['sourceCommit']!=expected_commit:raise ValueError('Unexpected source commit')
        if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?',manifest['version']):raise ValueError('Invalid version')
        if content['release-settings.json']!=settings_bytes or content['component-pins.json']!=component_bytes:raise ValueError('Self-authorized replacement pins are forbidden')
        listed={}
        for item in manifest['files']:
            name=item['path'];safe_name(name)
            if name in listed:raise ValueError('Duplicate manifest file')
            listed[name]=item
            data=content[name]
            if len(data)!=item['length'] or hashlib.sha256(data).hexdigest()!=item['sha256']:raise ValueError('Package file identity mismatch: '+name)
        if set(content)!=(set(listed)|{'package-manifest.json'}):raise ValueError('Unlisted or missing ZIP payload')
        notice_names={'Notices/'+item['name'] for item in settings['notices']}
        component_names={'Components/original-aspect-ratio-v1/'+item['name'] for item in components['components']+components['notices']}
        root_text={'README.txt','package-manifest.json','component-pins.json','release-settings.json','PowerOfChaos.Launcher.deps.json','PowerOfChaos.Launcher.runtimeconfig.json','LICENSE.txt','THIRD-PARTY-NOTICES.txt'}
        for name,data in content.items():
            if name in notice_names or name in component_names or name in root_text:continue
            if '/' in name or not (name.lower().endswith('.dll') or name in ('PowerOfChaos.Launcher.exe','createdump.exe')):raise ValueError('Unexpected published payload: '+name)
            machine(data)
        for prefix,pins in [('Notices/',settings['notices']),('Components/original-aspect-ratio-v1/',components['components']+components['notices'])]:
            for pin in pins:
                data=content[prefix+pin['name']]
                if len(data)!=pin['length'] or hashlib.sha256(data).hexdigest()!=pin['sha256']:raise ValueError('A pinned release dependency changed')
        target={'win-x64':0x8664,'win-x86':0x14c,'win-arm64':0xaa64}[manifest['runtimeIdentifier']]
        for name in ('PowerOfChaos.Launcher.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll'):
            if machine(content[name])!=target:raise ValueError('Wrong native runtime architecture')
        for name in ('ddraw.dll','winmm.dll'):
            if machine(content['Components/original-aspect-ratio-v1/'+name])!=0x14c:raise ValueError('Game-side display dependency is not x86')
        config=load(content['PowerOfChaos.Launcher.runtimeconfig.json'])['runtimeOptions']
        if 'framework' in config or 'frameworks' in config:raise ValueError('Global .NET runtime prerequisite remains')
        frameworks=config.get('includedFrameworks',[])
        if len(frameworks)!=1 or frameworks[0].get('name')!='Microsoft.NETCore.App' or frameworks[0].get('version')!=settings['runtimeVersion']:raise ValueError('Bundled framework differs from reviewed runtime')
        deps=load(content['PowerOfChaos.Launcher.deps.json'])['libraries']
        identities={item['id']+'/'+item['version']:item['type'] for item in manifest['dependencies']}
        if identities!={name:item['type'] for name,item in deps.items()}:raise ValueError('Dependency provenance mismatch')
    return dict(status='STRUCTURE_PASS',files=len(entries),expandedBytes=total,version=manifest['version'],sourceCommit=manifest['sourceCommit'],runtimeIdentifier=manifest['runtimeIdentifier'],runtimeVersion=manifest['runtimeVersion'],note='Executable smoke test is a separate required gate; no game was executed.')
def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--package',required=True);parser.add_argument('--expected-commit');args=parser.parse_args()
    try:print(json.dumps(inspect(Path(args.package),args.expected_commit)));return 0
    except (ValueError,KeyError,TypeError,OSError,zipfile.BadZipFile) as error:print('Release guard rejected package: '+str(error),file=sys.stderr);return 1
if __name__=='__main__':sys.exit(main())
