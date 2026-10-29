"""Generate bounded arithmetic PNG/reference fixtures; no artwork or game input.
Canonical GDI/allocator/guard-page fixture algorithms retain reviewed semantics.
Restricted-profile fixtures are freshly synthesized format counterexamples.
"""
from pathlib import Path
import binascii, hashlib, json, random, shutil, struct, zlib
from local_paths import require_private

def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()

def generate_gdi(fixtures):
    fixtures=require_private(fixtures,fresh=True)
    faultfixtures=require_private(fixtures.with_name(fixtures.name+'-fault'),fresh=True)
    fixtures.mkdir(parents=True)
    def chunk(t,d):return struct.pack('>I',len(d))+t+d+struct.pack('>I',binascii.crc32(t+d)&0xffffffff)
    def png(w=800,h=1160,rgba=False,smooth=False):
        rows=[]
        for y in range(h):
            row=bytearray([0])
            for x in range(w):
                # Repeated thin upright/cross/diagonal strokes approximate
                # high-contrast small lettering without any font/game input.
                ink=x%47<3 or (y%41<3 and x%47<29) or x%47==y%41
                row.extend(((0,0,0) if ink else (255,255,255)) if smooth else ((224,48,32) if ((x//2+y//2)&1) else (16,208,240)))
                row.extend([255] if rgba else [])
            rows.append(row)
        return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,6 if rgba else 2,0,0,0))+chunk(b'IDAT',zlib.compress(b''.join(rows)))+chunk(b'IEND',b'')
    for key in range(1,6):
        (fixtures/f'ref{key}.bin').write_bytes(bytes((i*37+i//400+key*19)%253 for i in range(116000)))
        (fixtures/f'master{key}.png').write_bytes(png())
    (fixtures/'zero.bin').write_bytes(bytes(116000));(fixtures/'short.bin').write_bytes(bytes(115999))
    data=bytearray(png());data[40]^=1;(fixtures/'corrupt.png').write_bytes(data)
    (fixtures/'rgba.png').write_bytes(png(rgba=True));(fixtures/'small.png').write_bytes(png(80,116))
    def cfg(name,game=3,rows=None,raw=None):
        if rows is None:rows=[(1,'ref1.bin','master1.png')]
        value='POC_HD_VISIBLE_V1\n'+str(game)+'\t1\n'+''.join(f'{k}\t{fixtures/r}\t{fixtures/m}\n' for k,r,m in rows)
        (fixtures/name).write_bytes(raw if raw is not None else value.encode())
    for n,g in [('joey',3),('yugi',1),('kaiba',2)]:cfg(n+'.tsv',g)
    (fixtures/'smooth.png').write_bytes(png(smooth=True));cfg('smooth.tsv',rows=[(1,'ref1.bin','smooth.png')])
    cfg('five.tsv',rows=[(k,f'ref{k}.bin',f'master{k}.png') for k in range(1,6)])
    cfg('zero.tsv',1,[(1,'zero.bin','master1.png')]);cfg('ambiguous.tsv',rows=[(1,'ref1.bin','master1.png'),(2,'ref1.bin','master2.png')])
    cfg('duplicatekey.tsv',rows=[(1,'ref1.bin','master1.png'),(1,'ref2.bin','master2.png')])
    cfg('missingref.tsv',rows=[(1,'absent.bin','master1.png')]);cfg('shortref.tsv',rows=[(1,'short.bin','master1.png')])
    cfg('zero-key.tsv',rows=[(0,'ref1.bin','master1.png')]);cfg('over1024.tsv',rows=[(k,'ref1.bin','master1.png') for k in range(1,1026)])
    for name,master in [('corrupt','corrupt.png'),('missingpng','absent.png'),('rgba','rgba.png'),('smallpng','small.png')]:
        cfg(name+'.tsv',rows=[(1,'ref1.bin',master)])
    # Full CRC-correct malformed compressed streams, including the exact
    # independently reported WIC 'bad zlib' counterexample.
    goodpng=png();idatlen=struct.unpack('>I',goodpng[33:37])[0];compressed=goodpng[41:41+idatlen]
    def repack(z):return goodpng[:33]+chunk(b'IDAT',z)+chunk(b'IEND',b'')
    # Decode fixture-only zlib using Python, then emit independent stored blocks.
    raw=zlib.decompress(compressed);stored=bytearray(b'\x78\x01')
    for at in range(0,len(raw),65535):
        block=raw[at:at+65535];stored.extend(b'\x00'+struct.pack('<HH',len(block),65535-len(block))+block)
    stored.extend(b'\x01\x00\x00\xff\xff'+struct.pack('>I',zlib.adler32(raw)&0xffffffff))
    mutations={'badzlib':b'bad zlib','badadler':compressed[:-1]+bytes([compressed[-1]^1]),'truncated':compressed[:-5]+compressed[-4:],'baddeflate':b'\x78\x01\x07'+compressed[-4:],'trailingidat':compressed[:-4]+b'junk'+compressed[-4:],'storedempty':bytes(stored),'trailingempty':bytes(stored[:-4])+b'junk'+bytes(stored[-4:]),'zipbomb':zlib.compress(bytes(64*1024*1024))}
    for name,z in mutations.items():
        (fixtures/(name+'.png')).write_bytes(repack(z));cfg(name+'.tsv',rows=[(1,'ref1.bin',name+'.png')])
    good=(fixtures/'joey.tsv').read_bytes()
    for name,data in [('empty',b''),('badheader',good.replace(b'POC_HD_VISIBLE_V1',b'NOPE')),('format555',good.replace(b'3\t1',b'3\t2')),('relative',good.replace(str(fixtures/'ref1.bin').encode(),b'relative.bin')),('trailing',good+b'\n'),('nul',good+b'\x00'),('device',good.replace(str(fixtures/'ref1.bin').encode(),b'\\\\.\\NUL')),('ads',good.replace(b'ref1.bin',b'ref1.bin:ads')),('dotdot',good.replace(b'ref1.bin',b'..\\ref1.bin')),('badutf8',good.replace(b'master1.png',b'master\xff.png'))]:cfg(name+'.tsv',raw=data)
    shutil.copytree(fixtures,faultfixtures)
    # Keep fault configs pointed to their own immutable fixture copies.
    for p in faultfixtures.glob('*.tsv'):p.write_bytes(p.read_bytes().replace(str(fixtures).encode(),str(faultfixtures).encode()))


def generate_allocator(D):
    D=require_private(D,fresh=True);D.mkdir(parents=True)
    def chunk(t,b):return struct.pack('>I',len(b))+t+b+struct.pack('>I',zlib.crc32(t+b)&0xffffffff)
    def png(raw,interlace=0,**kwargs):
     c=zlib.compressobj(**kwargs);z=c.compress(raw)+c.flush()
     return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',800,1160,8,2,0,0,interlace))+chunk(b'IDAT',z)+chunk(b'IEND',b''),z
    raw=b''.join(b'\0'+bytes([y%251])*2400 for y in range(1160))
    fixtures={}
    for name,args in [('fixed',{'strategy':zlib.Z_FIXED}),('dynamic',{}),('stored',{'level':0})]:
     b,z=png(raw,**args);assert ((z[2]>>1)&3)=={'fixed':1,'dynamic':2,'stored':0}[name]
     (D/(name+'.png')).write_bytes(b);fixtures[name]={'sha256':sha(D/(name+'.png')),'first_block_type':(z[2]>>1)&3,'expected':len(raw)}
    passes=[(0,0,8,8),(4,0,8,8),(0,4,4,8),(2,0,4,4),(0,2,2,4),(1,0,2,2),(0,1,1,2)]
    adam=b''.join((b'\0'+bytes(((800-x+dx-1)//dx)*3))*((1160-y+dy-1)//dy) for x,y,dx,dy in passes)
    b,z=png(adam,interlace=1);(D/'adam7.png').write_bytes(b);fixtures['adam7']={'sha256':sha(D/'adam7.png'),'expected':len(adam)}


def generate_regression(D):
    D=require_private(D,fresh=True);D.mkdir(parents=True)
    manifest=[]
    def chunk(t,b):return struct.pack('>I',len(b))+t+b+struct.pack('>I',zlib.crc32(t+b)&0xffffffff)
    header=struct.pack('>IIBBBBB',800,1160,8,2,0,0,0)
    sig=b'\x89PNG\r\n\x1a\n'
    def png(z,h=header,extra=b'',split=False,tail=b''):
     idats=chunk(b'IDAT',z) if not split else chunk(b'IDAT',z[:3])+chunk(b'IDAT',z[3:])
     return sig+chunk(b'IHDR',h)+extra+idats+chunk(b'IEND',b'')+tail
    def emit(name,b,valid):
     (D/name).write_bytes(b);manifest.append(f'{name}\t{int(valid)}\n')
    raw=b''.join(b'\0'+bytes([y%251])*2400 for y in range(1160));compressed=zlib.compress(raw)
    for filter in range(5):
     r=b''.join(bytes([filter])+bytes(2400) for y in range(1160))
     emit(f'filter{filter}.png',png(zlib.compress(r)),True)
    emit('filter5.png',png(zlib.compress(b'\5'+raw[1:])),False)
    emit('split-idat.png',png(compressed,split=True),True)
    emit('short-output.png',png(zlib.compress(raw[:-1])),False)
    emit('long-output.png',png(zlib.compress(raw+b'\0')),False)
    emit('tail-after-iend.png',png(compressed,tail=b'x'),False)
    emit('tail-before-adler.png',png(compressed[:-4]+b'\0'+compressed[-4:]),False)
    emit('second-zlib.png',png(compressed+compressed),False)
    emit('bad-adler.png',png(compressed[:-1]+bytes([compressed[-1]^128])),False)
    emit('bad-header.png',png(b'bad zlib'),False)
    emit('dictionary.png',png(b'\x78\x20'+compressed[2:]),False)
    emit('bad-crc.png',png(compressed)[:-1]+b'\0',False)
    emit('animation.png',png(compressed,extra=chunk(b'acTL',struct.pack('>II',2,0))),False)
    emit('transparency.png',png(compressed,extra=chunk(b'tRNS',bytes(6))),False)
    emit('unknown-critical.png',png(compressed,extra=chunk(b'ABCD',b'')),False)
    for n in [0,1,2,3,4,5,6,7,8,9,10,20,len(compressed)-5,len(compressed)-1]:emit(f'truncated-{n}.png',png(compressed[:n]),False)
    for n in [0,1,7,31,33,44]:emit(f'png-truncated-{n}.png',png(compressed)[:n],False)
    # Independently pack an Adam7 RGB8 image, with each pass using filter zero.
    passes=[(0,0,8,8),(4,0,8,8),(0,4,4,8),(2,0,4,4),(0,2,2,4),(1,0,2,2),(0,1,1,2)]
    adam=b''
    for x,y,dx,dy in passes:
     pw=(800-x+dx-1)//dx;ph=(1160-y+dy-1)//dy
     adam+=b''.join(b'\0'+bytes(pw*3) for _ in range(ph))
    emit('adam7.png',png(zlib.compress(adam),header[:-1]+b'\1'),True)
    # CRC-valid random compressed garbage kept tiny and guard-page adjacent.
    rng=random.Random(281026)
    for i in range(200):emit(f'random-{i}.png',png(b'\x78\x01'+rng.randbytes(i%90)+bytes(4)),False)
    emit('zipbomb.png',png(zlib.compress(bytes(64*1024*1024))),False)
    (D/'manifest.tsv').write_bytes(''.join(manifest).encode())



def generate_profile(D):
    D=require_private(D,fresh=True);D.mkdir(parents=True)
    def chunk(t,b): return struct.pack('>I',len(b))+t+b+struct.pack('>I',zlib.crc32(t+b)&0xffffffff)
    raw=b''.join(b'\0'+bytes([y%251])*2400 for y in range(1160))
    prefix=b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',800,1160,8,2,0,0,0))
    data=chunk(b'IDAT',zlib.compress(raw));end=chunk(b'IEND',b'');base=prefix+data+end
    rows=[];badnames=[]
    def emit(name,value,category=0):
        p=D/name;p.write_bytes(value);rows.append(str(p)+'\t'+str(category)+'\n')
        if category==0:badnames.append(name)
    # Synthetic equivalents of the reviewed chunk-type and framing regressions;
    # no reviewer fixture, original master list, artwork or private path is used.
    emit('type-digit.png',prefix+chunk(b'a1CD',b'')+data+end)
    emit('type-nul.png',prefix+chunk(b'a\0CD',b'')+data+end)
    emit('reserved-bit.png',prefix+chunk(b'abcd',b'')+data+end)
    emit('empty-gamma.png',prefix+chunk(b'gAMA',b'')+data+end)
    gamma=chunk(b'gAMA',struct.pack('>I',45455))
    emit('gamma-after-idat.png',prefix+data+gamma+end)
    emit('duplicate-gamma.png',prefix+gamma+gamma+data+end)
    for name,t,b in [('valid-gamma',b'gAMA',struct.pack('>I',45455)),('text',b'tEXt',b'key\0value'),('srgb',b'sRGB',b'\0'),('phys',b'pHYs',struct.pack('>IIB',2835,2835,1)),('palette',b'PLTE',bytes(3)),('background',b'bKGD',bytes(6)),('sbit',b'sBIT',bytes([8,8,8])),('unknown-ancillary',b'abCD',b'')]:
        emit(name+'.png',prefix+chunk(t,b)+data+end)
    for pos in range(4):
        for value in [0,1,31,48,57,64,91,96,123,127,128,255]:
            t=bytearray(b'abCD');t[pos]=value;emit(f'invalid-{pos}-{value}.png',prefix+chunk(bytes(t),b'')+data+end)
    emit('valid.png',base,1)
    emit('large-split.png',prefix+chunk(b'IDAT',bytes(12*1024*1024))+chunk(b'IDAT',bytes(3*1024*1024))+end,2)
    (D/'profile-manifest.tsv').write_text(''.join(rows),encoding='utf-8',newline='\n')
    (D/'ref1.bin').write_bytes(bytes((i*37+i//400+19)%253 for i in range(116000)))
    for name in badnames+['valid.png']:
        cfg='POC_HD_VISIBLE_V1\n3\t1\n1\t'+str(D/'ref1.bin')+'\t'+str(D/name)+'\n'
        (D/('good.tsv' if name=='valid.png' else name+'.tsv')).write_text(cfg,encoding='utf-8',newline='\n')
    (D/'gdi-manifest.txt').write_text(''.join(n+'.tsv\n' for n in badnames),encoding='utf-8',newline='\n')

def generate_decode(D):
    D=require_private(D,fresh=True);D.mkdir(parents=True)
    def chunk(t,b):return struct.pack('>I',len(b))+t+b+struct.pack('>I',zlib.crc32(t+b)&0xffffffff)
    def rgb(x,y):return bytes(((x*7+y*3)%256,(x*11+y*5)%256,(x*13+y*17)%256))
    def paeth(a,b,c):
        p=a+b-c;aa=abs(p-a);bb=abs(p-b);cc=abs(p-c)
        return a if aa<=bb and aa<=cc else b if bb<=cc else c
    def rows(xs,ys,mode):
        previous=bytes(len(xs)*3);out=bytearray()
        for index,y in enumerate(ys):
            row=b''.join(rgb(x,y)for x in xs);f=index%5 if mode==5 else mode;out.append(f)
            for i,value in enumerate(row):
                a=row[i-3]if i>=3 else 0;b=previous[i];c=previous[i-3]if i>=3 else 0
                predictor=(0,a,b,(a+b)//2,paeth(a,b,c))[f];out.append((value-predictor)%256)
            previous=row
        return out
    for name,mode in [('filter'+str(i),i)for i in range(5)]+[('mixed',5),('adam7',5)]:
        interlace=name=='adam7'
        if interlace:
            raw=b''.join(rows(list(range(x,800,dx)),list(range(y,1160,dy)),mode)for x,y,dx,dy in[(0,0,8,8),(4,0,8,8),(0,4,4,8),(2,0,4,4),(0,2,2,4),(1,0,2,2),(0,1,1,2)])
        else:raw=rows(list(range(800)),list(range(1160)),mode)
        z=zlib.compress(raw);half=len(z)//2
        png=b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',800,1160,8,2,0,0,int(interlace)))+chunk(b'IDAT',z[:half])+chunk(b'IDAT',z[half:])+chunk(b'IEND',b'')
        (D/(name+'.png')).write_bytes(png)


def generate_cursor(D):
    generate_gdi(D)
    sprites=[]
    for state in (1,2):
        pixels=bytearray(16384)
        for y in range(8,43):
            for x in range(8,32):
                q=15 if y==8 and x<18 else (x+y)%16
                i=(y*64+x)*4
                pixels[i:i+4]=bytes(((x*17+state*11)%256,(y*13+state*23)%256,(x*7+y*3+state*31)%256,q*17))
        p=D/f'cursor{state}.bgra';p.write_bytes(pixels);sprites.extend((str(p),hashlib.sha256(pixels).hexdigest()))
    for game,g in [('yugi',1),('kaiba',2),('joey',3)]:
        config='POC_HD_VISIBLE_V2\n'+str(g)+'\t1\nCURSORS_BGRA64_V1\t'+'\t'.join(sprites)+'\n1\t'+str(D/'ref1.bin')+'\t'+str(D/'master1.png')+'\n'
        (D/(game+'-cursor.tsv')).write_text(config,encoding='utf-8',newline='\n')
    # Explicit stale/invalid bank cases use fresh configuration reads.
    good=(D/'joey-cursor.tsv').read_text()
    (D/'bad-cursor-hash.tsv').write_text(good.replace(sprites[1],'0'*64),encoding='utf-8',newline='\n')
    (D/'missing-cursor.tsv').write_text(good.replace(sprites[0],str(D/'absent.bgra')),encoding='utf-8',newline='\n')

def generate_all(root):
    root=require_private(root,fresh=True);root.mkdir(parents=True)
    generate_gdi(root/'gdi')
    generate_allocator(root/'allocator')
    generate_regression(root/'regression')
    generate_profile(root/'profile')
    generate_decode(root/'decode')
    generate_cursor(root/'cursor')
    generate_flip(root/'flip')
    return [{'path':p.relative_to(root).as_posix(),'length':p.stat().st_size,'sha256':sha(p)}
            for p in sorted(root.rglob('*')) if p.is_file()]


def generate_flip(D):
    generate_cursor(D)
    backgrounds=[]
    for variant in (0,1):
        pixels=bytearray()
        for y in range(290):
            for x in range(200):
                value=(x*991+y*3169+0x1397)&65535
                if variant and 2<=x<198:value^=0x1234
                pixels.extend(struct.pack('<H',value))
        p=D/f'background{variant}.rgb565';p.write_bytes(pixels)
        backgrounds.extend((str(p),hashlib.sha256(pixels).hexdigest()))
    for game in ('yugi','kaiba','joey'):
        lines=(D/(game+'-cursor.tsv')).read_text().splitlines()
        lines[0]='POC_HD_VISIBLE_V3'
        lines=lines[:3]+['FLIP_BG565_V1\t'+'\t'.join(backgrounds),
            'FLIP_POSES_V1\t196,9,15\t184,15,14\t166,24,13\t141,37,12\t111,52,10\t76,69,7\t39,88,5']+lines[3:]
        (D/(game+'-flip.tsv')).write_text('\n'.join(lines)+'\n',encoding='utf-8',newline='\n')
    good=(D/'joey-flip.tsv').read_text()
    (D/'bad-flip-hash.tsv').write_text(good.replace(backgrounds[1],'0'*64),encoding='utf-8',newline='\n')
    (D/'bad-flip-pose.tsv').write_text(good.replace('184,15,14','183,15,14'),encoding='utf-8',newline='\n')
    # Producer-owned adversarial complete-raster ambiguity. The raw static B
    # includes a PARTIAL-alpha cursor already; B+cursor is not raw B. It must
    # still compete independently with A's flip+cursor explanation in V3.
    reference=(D/'ref1.bin').read_bytes();background=(D/'background0.rgb565').read_bytes()
    def mix(source,destination,q,shortcut):
        if shortcut and source==destination:return source
        value=0
        for shift,mask in ((0,31),(5,63),(11,31)):
            value|=((((source>>shift)&mask)*q//15+((destination>>shift)&mask)*(15-q)//15)<<shift)
        return value
    width,left,shade,ox,oy=111,45,10,103,205
    for state in (1,2):
        frame=bytearray([0x22])*960000;sprite=(D/f'cursor{state}.bgra').read_bytes()
        for y in range(290):
            for x in range(200):
                value=struct.unpack_from('<H',background,(y*200+x)*2)[0]
                if left<=x<left+width:
                    j=x-left;quotient,remainder=divmod(200,width)
                    sx=j*quotient+max(0,((j+1)*remainder-1)//width)
                    value=mix(struct.unpack_from('<H',reference,(y*200+sx)*2)[0],value,shade,True)
                struct.pack_into('<H',frame,((y+89)*800+x+7)*2,value)
        for y in range(64):
            for x in range(64):
                b,g,r,a=sprite[(y*64+x)*4:(y*64+x+1)*4];at=((y+oy)*800+x+ox)*2
                source=((r>>3)<<11)|((g>>2)<<5)|(b>>3)
                struct.pack_into('<H',frame,at,mix(source,struct.unpack_from('<H',frame,at)[0],a>>4,False))
        raw=b''.join(frame[((y+89)*800+7)*2:((y+89)*800+207)*2]for y in range(290))
        (D/f'ambiguous-state{state}.bin').write_bytes(raw)
        (D/f'ambiguous-state{state}-frame.rgb565').write_bytes(frame)
        for game in ('yugi','kaiba','joey'):
            cfg=(D/(game+'-flip.tsv')).read_text()+f'2\t{D/f"ambiguous-state{state}.bin"}\t{D/"smooth.png"}\n'
            (D/f'ambiguous-{game}-state{state}.tsv').write_text(cfg,encoding='utf-8',newline='\n')
