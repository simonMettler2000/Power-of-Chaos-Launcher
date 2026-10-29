"""Make a local V3 Deck-flip pack from V2 and explicit original-background crops.

Generated banks/configs are local proprietary data, never publication inputs.
The seven observable hypotheses are admitted only by complete current-pixel proof.
"""
import argparse
import hashlib
import json
from pathlib import Path
from hd_cards_build_visible_pack import no_links

POSES='FLIP_POSES_V1\t196,9,15\t184,15,14\t166,24,13\t141,37,12\t111,52,10\t76,69,7\t39,88,5'
GAMES={'yugi':1,'kaiba':2,'joey':3}


def pinned(path, size):
    path=Path(path)
    if not path.is_absolute():raise ValueError('Absolute local input required')
    no_links(path)
    if path.stat().st_size!=size:raise ValueError('Wrong bank byte count')
    data=path.read_bytes()
    if len(data)!=size:raise ValueError('Changed bank byte count')
    return data,hashlib.sha256(data).hexdigest()


def build(pack,backgrounds,output):
    pack,output=Path(pack).absolute(),Path(output).absolute()
    no_links(pack);no_links(output)
    if output.exists() or output.is_relative_to(pack):raise ValueError('Fresh separate output required')
    prepared=[]
    for game,number in GAMES.items():
        config=pack/(game+'-565.tsv');no_links(config)
        if not 1<=config.stat().st_size<=1024*1024:raise ValueError('Config bounds')
        lines=config.read_text(encoding='utf-8').splitlines()
        if not 4<=len(lines)<=1027 or lines[:2]!=['POC_HD_VISIBLE_V2',str(number)+'\t1']:raise ValueError('Exact V2 source pack required')
        cursors=lines[2].split('\t')
        if len(cursors)!=5 or cursors[0]!='CURSORS_BGRA64_V1':raise ValueError('Cursor bank grammar')
        for i in (1,3):
            _,digest=pinned(cursors[i],16384)
            if cursors[i+1]!=digest:raise ValueError('Cursor bank pin')
        paths=backgrounds.get(game,[])
        if not 1<=len(paths)<=2:raise ValueError('One/two explicit background crops per title required')
        values=[pinned(path,116000)for path in paths]
        if len(values)==1:values*=2
        keys=set()
        for line in lines[3:]:
            fields=line.split('\t')
            if len(fields)!=3 or not fields[0].isascii() or not fields[0].isdigit() or not 0<int(fields[0])<=0xffffffff or int(fields[0])in keys:raise ValueError('Reference row grammar')
            keys.add(int(fields[0]))
        prepared.append((game,lines,values))
    output.mkdir(parents=True)
    receipts=[]
    for game,lines,values in prepared:
        fields=['FLIP_BG565_V1']
        for i,(data,digest)in enumerate(values):
            path=output/(game+'-background'+str(i)+'.rgb565');path.write_bytes(data)
            fields.extend((str(path),digest));receipts.append({'game':game,'path':str(path),'sha256':digest,'bytes':len(data)})
        lines[0]='POC_HD_VISIBLE_V3'
        text='\n'.join(lines[:3]+['\t'.join(fields),POSES]+lines[3:])+'\n'
        (output/(game+'-565.tsv')).write_text(text,encoding='utf-8',newline='\n')
    (output/'flip-profile-receipt.json').write_text(json.dumps({'schema':'poc-hd-local-deck-flip-v3','backgrounds':receipts,
        'scope':'Local generated data; no all-pose/hardware/runtime/HD qualification'},indent=2)+'\n',encoding='utf-8',newline='\n')
    return {'success':True,'output':str(output),'games':3}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pack',required=True,type=Path);parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--background',action='append',required=True,help='game=absolute200x290RGB565crop; repeat for2variants')
    args=parser.parse_args();backgrounds={}
    for item in args.background:
        game,path=item.split('=',1)
        if game not in GAMES:raise ValueError('Unknown game')
        backgrounds.setdefault(game,[]).append(Path(path))
    print(json.dumps(build(args.pack,backgrounds,args.output)))


if __name__=='__main__':main()
