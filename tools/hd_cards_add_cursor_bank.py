"""Add verified LOCAL normal software cursor banks to a fresh visible pack.

Only project-authored extraction logic and factual hashes belong in source.
No original pixels, archives or generated banks may be published with tooling.
"""
import argparse, hashlib, json, os, stat, struct, sys
from pathlib import Path
from hd_cards_build_visible_pack import no_links
GAMES={'yugi':1,'kaiba':2,'joey':3}
CURSORS={
 'cursor00.yga':'81fa0dc775bdd71e9ffd95e8775a3d99f57bf0e23e9a08213f7bf217d5d38a34',
 'cursor01.yga':'5752b2298002f9bcf31cc722006bbc42a59fb54077afe72b8fc3e67c31e1b43c'}
def sha(data):return hashlib.sha256(data).hexdigest()
def expand(data,size):
    if size!=16384 or not 0<len(data)<=65536:raise ValueError('Unexpected cursor stream')
    window=bytearray(4096);cursor=0xfee;pos=0;flags=0;out=bytearray()
    while pos<len(data) and len(out)<size:
        flags>>=1
        if not flags&0x100:flags=data[pos]|0xff00;pos+=1
        if flags&1:
            if pos>=len(data):raise ValueError('Truncated literal')
            values=[data[pos]];pos+=1
        else:
            if pos+2>len(data):raise ValueError('Truncated pair')
            a,b=data[pos:pos+2];pos+=2;start=a|((b&0xf0)<<4);values=None
        count=len(values)if values is not None else (b&15)+3
        for i in range(count):
            value=values[i]if values is not None else window[(start+i)&4095]
            out.append(value);window[cursor]=value;cursor=(cursor+1)&4095
            if len(out)>size:raise ValueError('Cursor output overflow')
    if len(out)!=size or pos!=len(data):raise ValueError('Cursor stream extent mismatch')
    return bytes(out)
def read_cursors(path):
    no_links(path)
    if not path.is_file() or not 12<=path.stat().st_size<=1024*1024*1024:raise ValueError('Archive file bound')
    with path.open('rb')as f:
        head=f.read(12)
        if head[:8]!=b'KCEJYUGI':raise ValueError('Archive signature')
        count,=struct.unpack_from('<I',head,8)
        if not 1<=count<=100000 or 12+268*count>path.stat().st_size:raise ValueError('Archive directory bound')
        directory=f.read(268*count);found={}
        for i in range(count):
            row=directory[i*268:(i+1)*268];name=bytes(((v&15)<<4)|(v>>4)for v in row[:256]).split(b'\0',1)[0].decode('cp1252').lower()
            if name not in ['cursor\\'+n for n in CURSORS]:continue
            if name in found:raise ValueError('Duplicate cursor resource')
            off,size,stored=struct.unpack_from('<III',row,256)
            if size>65536 or stored>65536 or off<12+268*count or off+stored>path.stat().st_size:raise ValueError('Cursor archive bounds')
            f.seek(off);payload=f.read(stored)
            if size!=stored:raise ValueError('Known cursor payload expected stored YGA')
            short=name.split('\\')[-1]
            if sha(payload)!=CURSORS[short]:raise ValueError('Unqualified cursor payload profile')
            if len(payload)<24 or struct.unpack_from('<6I',payload)[:5]!=(0x616779,64,64,1,16384):raise ValueError('Cursor YGA header')
            if struct.unpack_from('<I',payload,20)[0]!=len(payload)-24:raise ValueError('YGA stream length')
            found[short]=expand(payload[24:],16384)
    if set(found)!=set(CURSORS):raise ValueError('Missing normal cursor state')
    return found

def build(pack,archives,output):
    pack=Path(os.path.abspath(pack));output=Path(os.path.abspath(output));no_links(pack);no_links(output)
    if output.exists() or not pack.is_dir() or output==pack or output.is_relative_to(pack):raise ValueError('Fresh output outside input pack required')
    if set(archives)!=set(GAMES):raise ValueError('Explicit original archive required for all three games')
    prepared=[]
    for game in GAMES:
        config=pack/(game+'-565.tsv');no_links(config)
        if config.stat().st_size>1024*1024:raise ValueError('Config bound')
        lines=config.read_text(encoding='utf-8-sig').splitlines()
        if len(lines)<3 or lines[0]!='POC_HD_VISIBLE_V1' or lines[1]!=str(GAMES[game])+'\t1':raise ValueError('Expected original V1 RGB565 game config')
        cursors=read_cursors(Path(os.path.abspath(archives[game])));prepared.append((game,lines,cursors))
    output.mkdir();receipts=[]
    for game,lines,cursors in prepared:
        fields=['CURSORS_BGRA64_V1']
        for name in CURSORS:
            p=output/(game+'-'+name[:-4]+'.bgra');p.write_bytes(cursors[name]);fields.extend((str(p),sha(cursors[name])))
        value=['POC_HD_VISIBLE_V2',lines[1],'\t'.join(fields),*lines[2:]]
        (output/(game+'-565.tsv')).write_text('\n'.join(value)+'\n',encoding='utf-8',newline='\n');receipts.append({'game':game,'cursor_raw_sha256':[fields[2],fields[4]]})
    (output/'cursor-bank-receipt.json').write_text(json.dumps({'schema':'poc-hd-local-normal-cursor-bank-v1','records':receipts,'scope':'locally extracted proprietary sprites; never publish generated files'},indent=2)+'\n')
def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--pack',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    p.add_argument('--archive',action='append',required=True,help='game=absolute original archive path; repeat for all three')
    a=p.parse_args();archives={}
    for item in a.archive:
        game,sep,path=item.partition('=')
        if not sep or game not in GAMES or game in archives or not Path(path).is_absolute():raise ValueError('Explicit unique game=absolute archive required')
        archives[game]=path
    build(a.pack,archives,a.output);print('PASS: fresh local-only normal cursor bank and V2 configs')
if __name__=='__main__':
    try:main()
    except (OSError,ValueError,KeyError,struct.error,UnicodeError)as e:sys.exit('Cursor pack: '+str(e))
