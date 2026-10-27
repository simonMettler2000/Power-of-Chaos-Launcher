"""Reject unknown provider source before any project is built (metadata only)."""
from pathlib import Path
import hashlib,json,sys
import sys
sys.dont_write_bytecode = True
from local_paths import require_private,no_tree_links

def verify_tree(root,kind):
    root=Path(root);no_tree_links(root)
    pins=json.loads((Path(__file__).parent/'component-pins.json').read_text(encoding='utf-8-sig'))['providerSourceTrees'][kind]
    rows=[(p.relative_to(root).as_posix(),p.stat().st_size,hashlib.sha256(p.read_bytes()).hexdigest()) for p in root.rglob('*') if p.is_file()]
    digest=hashlib.sha256(''.join('%s\t%s\t%s\n'%x for x in sorted(rows)).encode()).hexdigest()
    if len(rows)!=pins['count'] or digest!=pins['sha256']:raise ValueError('Exact provider source tree pin mismatch: '+kind)
if __name__=='__main__':
    verify_tree(require_private(sys.argv[1]),sys.argv[2]);print('PASS: exact '+sys.argv[2]+' provider source tree')
