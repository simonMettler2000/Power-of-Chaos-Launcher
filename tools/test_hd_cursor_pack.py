"""Procedural local cursor pack tests; no archive/game assets or execution."""
from pathlib import Path
import hashlib,json,sys
from unittest import mock
import hd_cards_add_cursor_bank as tool

def main(root):
    root=Path(root).resolve();root.mkdir(parents=True,exist_ok=False);checks=0
    def check(value):
        nonlocal checks
        assert value;checks+=1
    raw=bytes((i*37+i//256)%256 for i in range(16384));literal=b''.join(b'\xff'+raw[i:i+8]for i in range(0,len(raw),8))
    check(tool.expand(literal,16384)==raw)
    for data,size in [(literal[:-1],16384),(literal+b'\0',16384),(literal,16383),(b'\xff',16384),(b'\0\x01',16384)]:
        try:tool.expand(data,size)
        except ValueError:checks+=1
        else:raise AssertionError('Malformed cursor stream admitted')
    pack=root/'v1';pack.mkdir()
    for game,number in tool.GAMES.items():(pack/(game+'-565.tsv')).write_text(f'POC_HD_VISIBLE_V1\n{number}\t1\n1\tC:\\authored-ref.bin\tC:\\authored-master.png\n')
    sprites={'cursor00.yga':raw,'cursor01.yga':raw[::-1]};archives={g:root/(g+'.dat')for g in tool.GAMES}
    with mock.patch.object(tool,'read_cursors',return_value=sprites):tool.build(pack,archives,root/'v2')
    for game,number in tool.GAMES.items():
        rows=(root/'v2'/(game+'-565.tsv')).read_text().splitlines();fields=rows[2].split('\t')
        check(rows[0]=='POC_HD_VISIBLE_V2'and rows[1]==f'{number}\t1'and len(rows)==4)
        check(fields[0]=='CURSORS_BGRA64_V1'and len(fields)==5)
        for state in (1,2):check(hashlib.sha256(Path(fields[state*2-1]).read_bytes()).hexdigest()==fields[state*2])
    try:tool.build(pack,archives,root/'v2')
    except ValueError:checks+=1
    else:raise AssertionError('Existing output overwritten')
    for name,data in [('signature',b'bad archive!'),('count',b'KCEJYUGI'+(100001).to_bytes(4,'little'))]:
        path=root/(name+'.dat');path.write_bytes(data)
        try:tool.read_cursors(path)
        except ValueError:checks+=1
        else:raise AssertionError('Invalid archive admitted')
    print(json.dumps({'success':True,'checks':checks,'procedural_only':True}))
if __name__=='__main__':main(sys.argv[1])
