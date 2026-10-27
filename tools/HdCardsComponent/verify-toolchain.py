"""Check exact build tools and complete header/library/VC targets metadata."""
from pathlib import Path
import hashlib,json
import sys
sys.dont_write_bytecode = True
from local_paths import no_links,no_tree_links

def digest(p):
    h=hashlib.sha256()
    with p.open('rb') as f:
        for block in iter(lambda:f.read(1024*1024),b''):h.update(block)
    return h.hexdigest()

def main():
    pins=json.loads((Path(__file__).parent/'toolchain-pins.json').read_text(encoding='utf-8-sig'))
    for name,pin in pins['tools'].items():
        p=no_links(name)
        if p.stat().st_size!=pin['length'] or digest(p)!=pin['sha256']:raise ValueError('Tool pin mismatch: '+str(p))
    for name,pin in pins['directories'].items():
        base=Path(name);no_tree_links(base)
        rows=[(p.relative_to(base).as_posix(),p.stat().st_size,digest(p)) for p in base.rglob('*') if p.is_file()]
        actual=hashlib.sha256(''.join('%s\t%s\t%s\n'%x for x in sorted(rows)).encode()).hexdigest()
        if len(rows)!=pin['count'] or actual!=pin['sha256']:raise ValueError('Dependency directory pin mismatch: '+str(base))
    print('PASS: pinned tools and complete build dependency directories')
if __name__=='__main__':main()
