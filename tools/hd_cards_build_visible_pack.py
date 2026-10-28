"""Build local visible-raster references; no artwork or generated pack belongs in Git."""
import argparse, collections, hashlib, json, os, stat, struct, sys, zlib
from pathlib import Path

GAMES={'yugi':1,'kaiba':2,'joey':3}
DELIVERY_SCHEMAS={
    'power-of-chaos-reconstructed-card-delivery-v1',
    'power-of-chaos-complete-collection-serif-v2-manifest-v1',
}

def sha(data):return hashlib.sha256(data).hexdigest()

def no_links(path):
    for p in (path,*path.parents):
        if p.is_symlink() or (p.exists() and getattr(p.lstat(),'st_file_attributes',0)&getattr(stat,'FILE_ATTRIBUTE_REPARSE_POINT',1024)):
            raise ValueError('Links are not supported: '+str(p))

def relative(base,name):
    if not isinstance(name,str) or '\\' in name or not name or len(name)>1024:
        raise ValueError('Expected a portable relative asset path')
    for part in name.split('/'):
        if not part or part in ('.','..') or part!=part.rstrip('. ') or any(ord(c)<32 or c in ':<>"|?*' for c in part):
            raise ValueError('Unsafe asset path')
        stem=part.split('.')[0].rstrip(' ').upper()
        if stem in ('CON','PRN','AUX','NUL') or (len(stem)==4 and stem[:3] in ('COM','LPT') and stem[3] in '123456789¹²³'):
            raise ValueError('Device names are not assets')
    p=base/name;no_links(p)
    if not p.resolve().is_relative_to(base.resolve()):raise ValueError('Asset escapes root')
    return p

def read_pinned(path,expected,limit):
    no_links(path)
    if not path.is_file() or not 1<=path.stat().st_size<=limit:raise ValueError('Missing or oversized asset')
    data=path.read_bytes()
    if len(data)>limit or sha(data)!=expected:raise ValueError('Asset hash mismatch: '+str(path))
    return data

def decode_rgb(png):
    if png[:8]!=b'\x89PNG\r\n\x1a\n':raise ValueError('Not PNG')
    at=8;header=None;compressed=bytearray();ended=False;data_ended=False;seen_data=False
    while at<len(png):
        if len(png)-at<12:raise ValueError('Truncated PNG chunk')
        length,=struct.unpack_from('>I',png,at)
        if length>len(png)-at-12:raise ValueError('PNG chunk overflow')
        kind=png[at+4:at+8];body=png[at+8:at+8+length]
        if zlib.crc32(kind+body)!=struct.unpack_from('>I',png,at+8+length)[0]:raise ValueError('Bad PNG CRC')
        if not all(65<=c<=90 or 97<=c<=122 for c in kind) or kind[2]&32:raise ValueError('Bad chunk name')
        if header is None and kind!=b'IHDR':raise ValueError('IHDR must be first')
        if seen_data and kind!=b'IDAT':data_ended=True
        if kind==b'IHDR':
            if header is not None or length!=13:raise ValueError('Bad IHDR')
            w,h,depth,color,comp,filter_method,interlace=struct.unpack('>IIBBBBB',body)
            if (w,h) not in ((200,290),(199,290)) or (depth,color,comp,filter_method,interlace)!=(8,2,0,0,0):
                raise ValueError('Expected original RGB8 199/200 by290 PNG')
            header=(w,h)
        elif kind==b'IDAT':
            if data_ended:raise ValueError('Nonconsecutive IDAT')
            compressed.extend(body);seen_data=True
        elif kind==b'IEND':
            if length or not seen_data or at+12!=len(png):raise ValueError('Bad IEND/trailing data')
            ended=True
        elif kind in (b'tRNS',b'acTL',b'fcTL',b'fdAT') or not kind[0]&32 and kind!=b'PLTE':
            raise ValueError('Unsupported PNG feature')
        at+=length+12
    if not ended or header is None:raise ValueError('Incomplete PNG')
    w,h=header;stride=w*3;expected=(stride+1)*h
    decoder=zlib.decompressobj();raw=decoder.decompress(bytes(compressed),expected+1)
    if len(raw)!=expected or not decoder.eof or decoder.unused_data or decoder.unconsumed_tail:
        raise ValueError('Invalid or oversized PNG expansion')
    result=bytearray(w*h*3);previous=bytearray(stride)
    for y in range(h):
        offset=y*(stride+1);f=raw[offset];row=bytearray(raw[offset+1:offset+1+stride])
        if f>4:raise ValueError('Unknown filter')
        for x in range(stride):
            a=row[x-3] if x>=3 else 0;b=previous[x];c=previous[x-3] if x>=3 else 0
            if f==0:p=0
            elif f==1:p=a
            elif f==2:p=b
            elif f==3:p=(a+b)//2
            else:
                q=a+b-c;da,db,dc=abs(q-a),abs(q-b),abs(q-c)
                p=a if da<=db and da<=dc else b if db<=dc else c
            row[x]=(row[x]+p)&255
        result[y*stride:(y+1)*stride]=row;previous=row
    return w,h,bytes(result)

def packed16(rgb,fmt):
    if fmt not in (1,2) or len(rgb)%3:raise ValueError('Bad format/pixels')
    result=bytearray(len(rgb)//3*2)
    for i in range(0,len(rgb),3):
        r,g,b=rgb[i:i+3]
        value=((r>>3)<<11)|((g>>2)<<5)|(b>>3) if fmt==1 else ((r>>3)<<10)|((g>>3)<<5)|(b>>3)
        struct.pack_into('<H',result,i//3*2,value)
    return bytes(result)

def build(delivery,source_root,output,games,formats):
    delivery=Path(os.path.abspath(delivery));source_root=Path(os.path.abspath(source_root));output=Path(os.path.abspath(output))
    no_links(delivery);no_links(source_root);no_links(output)
    if output.is_relative_to(delivery.parent):raise ValueError('Output cannot modify read-only delivery')
    if delivery.stat().st_size>16*1024*1024:raise ValueError('Manifest too large')
    d=json.loads(delivery.read_text(encoding='utf-8-sig'))
    if d.get('schema') not in DELIVERY_SCHEMAS or not 1<=len(d['entries'])<=4096:
        raise ValueError('Unknown delivery')
    output.mkdir(parents=True,exist_ok=False);refs=output/'refs';refs.mkdir()
    rows=collections.defaultdict(list);records=[];skips=[];seen=set();written=set()
    for entry in d['entries']:
        if entry.get('state')!='accepted':raise ValueError('Unaccepted artwork')
        master=relative(delivery.parent,entry['output']['path'])
        master_bytes=read_pinned(master,entry['output']['sha256'],16*1024*1024)
        if master_bytes[:16]!=b'\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR' or struct.unpack('>IIBBBBB',master_bytes[16:29])!=(800,1160,8,2,0,0,0):
            raise ValueError('Wrong master format')
        for original in entry['originals']:
            game=original['game']
            if game not in games:continue
            path=relative(source_root,original['path'])
            png=read_pinned(path,original['sha256'],4*1024*1024)
            w,h,rgb=decode_rgb(png)
            if (w,h)!=(200,290):
                skips.append({'game':game,'original':str(path),'dimensions':[w,h],'reason':'No speculative background padding for199-wideoriginal'})
                continue
            for fmt in formats:
                reference=packed16(rgb,fmt);refhash=sha(reference)
                identity=(game,fmt,refhash,entry['output']['sha256'])
                if identity in seen:continue
                seen.add(identity);ref=refs/(str(fmt)+'-'+refhash+'.bin')
                if ref not in written:
                    with ref.open('xb') as stream:stream.write(reference)
                    written.add(ref)
                rows[(game,fmt)].append((ref,master,refhash,entry['output']['sha256'],entry.get('original_card_id'),entry['variant_key']))
    for game in games:
        for fmt in formats:
            selected=rows[(game,fmt)]
            if not 1<=len(selected)<=1024:raise ValueError('No or excessive references')
            config=output/(game+('-565.tsv' if fmt==1 else '-555.tsv'))
            lines=['POC_HD_VISIBLE_V1',str(GAMES[game])+'\t'+str(fmt)]
            for key,(reference,master,rhash,mhash,card_id,art_id) in enumerate(selected,1):
                if any(c in str(reference)+str(master) for c in '\t\r\n'):raise ValueError('TSV path control character')
                lines.append(str(key)+'\t'+str(reference)+'\t'+str(master))
                records.append({'game':game,'format':fmt,'key':key,'reference':str(reference),'referenceSha256':rhash,
                                'master':str(master),'masterSha256':mhash,'cardId':card_id,'artId':art_id})
            config.write_text('\n'.join(lines)+'\n',encoding='utf-8')
    collision=collections.defaultdict(set)
    for item in records:collision[(item['game'],item['format'],item['referenceSha256'])].add(item['masterSha256'])
    report={'schema':'authored-visible-card-pack-v1','deliveryManifestSha256':sha(delivery.read_bytes()),
            'records':records,'skipped':skips,'referenceFiles':len(written),
            'ambiguousVisibleGroups':sum(len(values)>1 for values in collision.values()),
            'scope':'Local exact original-raster references, no game execution or observed in-game match claim'}
    (output/'pack-receipt.json').write_text(json.dumps(report,indent=2)+'\n')
    return {'success':True,'mappings':len(records),'referenceFiles':len(written),'skipped':len(skips),
            'ambiguousVisibleGroups':report['ambiguousVisibleGroups'],'output':str(output)}

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--delivery',required=True,type=Path);p.add_argument('--source-root',required=True,type=Path)
    p.add_argument('--output',required=True,type=Path);p.add_argument('--game',choices=['all',*GAMES],default='all')
    p.add_argument('--format',choices=['both','565','555'],default='both');a=p.parse_args()
    games=list(GAMES) if a.game=='all' else [a.game];formats=[1,2] if a.format=='both' else [1 if a.format=='565' else 2]
    print(json.dumps(build(a.delivery,a.source_root,a.output,games,formats)))

if __name__=='__main__':
    try:main()
    except (OSError,ValueError,KeyError,TypeError,zlib.error,struct.error) as e:sys.exit('Visible pack: '+str(e))
