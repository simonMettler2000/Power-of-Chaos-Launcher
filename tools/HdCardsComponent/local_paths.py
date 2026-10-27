"""Require fresh, ignored .local output under an enclosing Git checkout."""
from pathlib import Path
import os,stat,subprocess

def no_links(path):
    path=Path(os.path.abspath(path))
    for p in (path,*path.parents):
        if p.is_symlink() or (p.exists() and getattr(p.lstat(),'st_file_attributes',0)&getattr(stat,'FILE_ATTRIBUTE_REPARSE_POINT',1024)):
            raise ValueError('Linked paths are not supported: '+str(p))
    return path

def require_private(path,fresh=False):
    path=no_links(path)
    checkout=next((p for p in path.parents if (p/'.git').exists()),None)
    if checkout is None or not path.is_relative_to(checkout/'.local') or path==checkout/'.local':
        raise ValueError('Output/source must be inside repository .local scratch')
    proof=subprocess.run(['git','-C',str(checkout),'check-ignore','--',str(path/'ignore-proof')],capture_output=True,check=False)
    if proof.returncode:raise ValueError('Output/source must be ignored')
    if fresh and path.exists():raise ValueError('Destination must be new')
    return path

def no_tree_links(path):
    no_links(path)
    for current,dirs,files in os.walk(path,followlinks=False):
        for name in dirs+files:no_links(Path(current)/name)
