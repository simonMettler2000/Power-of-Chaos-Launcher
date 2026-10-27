"""Authored RGB fixtures for local reference generation; no game or real artwork."""
import hashlib,importlib.util,json,struct,tempfile,unittest,zlib
from pathlib import Path
P=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('pack',P.parent/'tools/hd_cards_build_visible_pack.py');pack=importlib.util.module_from_spec(spec);spec.loader.exec_module(pack)

def png(w,h,rgb,filter_kind=0):
    def chunk(k,v):return struct.pack('>I',len(v))+k+v+struct.pack('>I',zlib.crc32(k+v))
    raw=bytearray();stride=w*3;prev=bytes(stride)
    for y in range(h):
        row=rgb[y*stride:(y+1)*stride];encoded=bytearray(stride)
        for i,v in enumerate(row):
            left=row[i-3] if i>=3 else 0;up=prev[i];corner=prev[i-3] if i>=3 else 0
            if filter_kind==0:prediction=0
            elif filter_kind==1:prediction=left
            elif filter_kind==2:prediction=up
            elif filter_kind==3:prediction=(left+up)//2
            else:
                q=left+up-corner;dist=[abs(q-left),abs(q-up),abs(q-corner)];prediction=(left,up,corner)[dist.index(min(dist))]
            encoded[i]=(v-prediction)&255
        raw.append(filter_kind);raw.extend(encoded);prev=row
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(raw))+chunk(b'IEND',b'')

class PackTests(unittest.TestCase):
    def test_all_filters_exact_rgb(self):
        rgb=bytes((i*31+i//19)%256 for i in range(200*290*3))
        for f in range(5):
            with self.subTest(filter=f):self.assertEqual((200,290,rgb),pack.decode_rgb(png(200,290,rgb,f)))

    def test_quantization_and_endianness(self):
        rgb=bytes([255,0,0,0,255,0,0,0,255,255,255,255,0,0,0])
        self.assertEqual(struct.pack('<5H',0xf800,0x07e0,0x001f,0xffff,0),pack.packed16(rgb,1))
        self.assertEqual(struct.pack('<5H',0x7c00,0x03e0,0x001f,0x7fff,0),pack.packed16(rgb,2))
        with self.assertRaises(ValueError):pack.packed16(rgb,3)

    def test_crc_truncation_and_expansion(self):
        good=png(200,290,bytes(200*290*3))
        for bad in (good[:-1],good+b'trailing',good[:20]+bytes([good[20]^1])+good[21:]):
            with self.assertRaises(ValueError):pack.decode_rgb(bad)
        with self.assertRaises(ValueError):pack.decode_rgb(png(800,1160,bytes(800*1160*3)))

    def test_per_game_tsv_dedup_and_readonly_assets(self):
        scratch=P.parent/'.local/hd-packaging-check/pack-tests';scratch.mkdir(parents=True,exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temp:
            root=Path(temp)
            self.assertTrue(root.resolve().is_relative_to(scratch.resolve()) and root.resolve()!=scratch.resolve())
            delivery=root/'delivery';(delivery/'images').mkdir(parents=True);original=root/'original.png'
            data=png(200,290,bytes([255,0,0])*200*290);original.write_bytes(data)
            master=png(800,1160,bytes([0,255,0])*800*1160);(delivery/'images/master.png').write_bytes(master)
            entry={'state':'accepted','original_card_id':0,'variant_key':'authored',
                   'output':{'path':'images/master.png','sha256':hashlib.sha256(master).hexdigest()},
                   'originals':[{'game':g,'path':'original.png','sha256':hashlib.sha256(data).hexdigest()} for g in pack.GAMES]}
            manifest=delivery/'manifest.json';manifest.write_text(json.dumps({'schema':'power-of-chaos-reconstructed-card-delivery-v1','entries':[entry]}))
            out=root/'out';result=pack.build(manifest,root,out,list(pack.GAMES),[1,2])
            self.assertEqual(6,result['mappings']);self.assertEqual(2,result['referenceFiles'])
            for g,number in pack.GAMES.items():
                for fmt,suffix in ((1,'565'),(2,'555')):
                    lines=(out/(g+'-'+suffix+'.tsv')).read_text().splitlines()
                    self.assertEqual(['POC_HD_VISIBLE_V1',str(number)+'\t'+str(fmt)],lines[:2])
                    key,ref,path=lines[2].split('\t');self.assertEqual('1',key)
                    self.assertEqual(116000,Path(ref).stat().st_size);self.assertEqual(delivery/'images/master.png',Path(path))
            self.assertEqual(data,original.read_bytes());self.assertEqual(master,(delivery/'images/master.png').read_bytes())
            with self.assertRaises(FileExistsError):pack.build(manifest,root,out,list(pack.GAMES),[1])

    def test_unsafe_paths_before_io(self):
        for name in ('../x.png','a//x.png','/x.png','a/NUL.png','COM¹.png','lpt2 .png','C:/x.png','a\\x.png'):
            with self.subTest(path=name),self.assertRaises(ValueError):pack.relative(P,name)

if __name__=='__main__':unittest.main()
