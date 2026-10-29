"""Procedural V3 pack grammar/pin/fresh-output tests; no game resources."""
import argparse
import hashlib
import tempfile
import unittest
from pathlib import Path
import hd_cards_add_flip_profile as tool

parser=argparse.ArgumentParser();parser.add_argument('--root',required=True,type=Path)
args=parser.parse_args();args.root=args.root.absolute();tool.no_links(args.root);args.root.mkdir(parents=True,exist_ok=True)


class FlipPack(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(dir=args.root);self.root=Path(self.temp.name);self.pack=self.root/'pack';self.pack.mkdir()
        sprite=self.pack/'normal.bgra';sprite.write_bytes(bytes(16384));sha=hashlib.sha256(sprite.read_bytes()).hexdigest()
        ref=self.pack/'reference.bin';ref.write_bytes(bytes(116000));master=self.pack/'master.png';master.write_bytes(b'procedural placeholder; metadata not decoded by profile adapter')
        self.backgrounds={}
        for game,number in tool.GAMES.items():
            (self.pack/(game+'-565.tsv')).write_text(f'POC_HD_VISIBLE_V2\n{number}\t1\nCURSORS_BGRA64_V1\t{sprite}\t{sha}\t{sprite}\t{sha}\n1\t{ref}\t{master}\n',encoding='utf-8',newline='\n')
            bg=self.root/(game+'.rgb565');bg.write_bytes(bytes([number])*116000);self.backgrounds[game]=[bg]
    def tearDown(self):self.temp.cleanup()
    def test_all_three_explicit_inputs(self):
        out=self.root/'out';tool.build(self.pack,self.backgrounds,out)
        for game in tool.GAMES:
            lines=(out/(game+'-565.tsv')).read_text().splitlines();self.assertEqual(lines[0],'POC_HD_VISIBLE_V3');self.assertEqual(lines[4],tool.POSES)
            fields=lines[3].split('\t');self.assertEqual(Path(fields[1]).read_bytes(),self.backgrounds[game][0].read_bytes());self.assertEqual(fields[2],fields[4])
        with self.assertRaises(ValueError):tool.build(self.pack,self.backgrounds,out)
    def test_short_background(self):
        self.backgrounds['joey'][0].write_bytes(bytes(115999))
        with self.assertRaises(ValueError):tool.build(self.pack,self.backgrounds,self.root/'out')
    def test_missing_title(self):
        del self.backgrounds['kaiba']
        with self.assertRaises(ValueError):tool.build(self.pack,self.backgrounds,self.root/'out')
    def test_unknown_source_version(self):
        p=self.pack/'yugi-565.tsv';p.write_text(p.read_text().replace('POC_HD_VISIBLE_V2','POC_HD_VISIBLE_V9'))
        with self.assertRaises(ValueError):tool.build(self.pack,self.backgrounds,self.root/'out')
    def test_tampered_cursor(self):
        (self.pack/'normal.bgra').write_bytes(bytes([1])*16384)
        with self.assertRaises(ValueError):tool.build(self.pack,self.backgrounds,self.root/'out')
    def test_unknown_cursor_grammar(self):
        p=self.pack/'joey-565.tsv';p.write_text(p.read_text().replace('CURSORS_BGRA64_V1','CURSORS_BGRA64_V2'))
        with self.assertRaises(ValueError):tool.build(self.pack,self.backgrounds,self.root/'out')
    def test_duplicate_key(self):
        p=self.pack/'joey-565.tsv';p.write_text(p.read_text()+p.read_text().splitlines()[-1]+'\n')
        with self.assertRaises(ValueError):tool.build(self.pack,self.backgrounds,self.root/'out')
    def test_output_inside_input_refused(self):
        with self.assertRaises(ValueError):tool.build(self.pack,self.backgrounds,self.pack/'child')


unittest.main(argv=['test_hd_flip_pack.py'])
