"""Authored metadata/header fixtures only; full decode is the C# decoder's responsibility."""
import hashlib
import importlib.util
import json
import struct
import tempfile
import unittest
from unittest import mock
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('hd_cards_import', ROOT / 'tools/hd_cards_import.py')
importer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(importer)

class ImportTests(unittest.TestCase):
    def setUp(self):
        self.fixture_parent = ROOT / '.local' / 'hd-import-tests'
        self.fixture_parent.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=self.fixture_parent)
        self.root = Path(self.temporary.name)
        self.delivery = self.root / 'delivery'
        images = self.delivery / 'images'
        images.mkdir(parents=True)
        # Deliberately just a header fixture: importer promises hashes/header, not PNG decode.
        self.png = b'\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR' + struct.pack('>IIBBBBB', 800, 1160, 8, 2, 0, 0, 0) + bytes(20)
        (images / 'authored.png').write_bytes(self.png)
        self.entry = dict(state='accepted', task_key='front:0', original_card_id=0,
                          variant_key='0123456789abcdef',
                          output=dict(path='images/authored.png', bytes=len(self.png), size=[800, 1160],
                                      sha256=hashlib.sha256(self.png).hexdigest()),
                          originals=[dict(game=game, sha256='0123456789abcdef' + '0' * 48) for game in ('yugi', 'kaiba', 'joey')])
        self.source = self.delivery / 'manifest.json'
        self.output = self.root / 'pack.json'

    def tearDown(self):
        resolved = self.root.resolve()
        self.assertTrue(resolved.is_relative_to(self.fixture_parent.resolve()) and resolved != self.fixture_parent.resolve())
        self.temporary.cleanup()

    def write(self, entries=None):
        self.source.write_text(json.dumps(dict(schema='power-of-chaos-reconstructed-card-delivery-v1',
                                               entries=entries if entries is not None else [self.entry])))

    def test_explicit_three_game_mapping_and_no_art_copy(self):
        self.write()
        result = importer.import_delivery(self.source, self.output)
        self.assertEqual(3, result['mappings'])
        manifest = json.loads(self.output.read_text())
        self.assertEqual({'yugi', 'kaiba', 'joey'}, {x['game'] for x in manifest['entries']})
        self.assertEqual({'authored.png'}, {x['path'] for x in manifest['entries']})
        self.assertEqual(1, len(list((self.delivery / 'images').iterdir())))

    def test_cannot_write_inside_readonly_delivery(self):
        self.write()
        with self.assertRaises(ValueError):
            importer.import_delivery(self.source, self.delivery / 'pack.json')

    def test_no_overwrite(self):
        self.write()
        self.output.write_text('preserve me')
        with self.assertRaises(FileExistsError):
            importer.import_delivery(self.source, self.output)
        self.assertEqual('preserve me', self.output.read_text())

    def test_traversal_and_missing_bad_hash(self):
        for path in ('../authored.png', 'images/../authored.png', 'images\\authored.png', 'images/a:b.png'):
            with self.subTest(path=path):
                self.entry['output']['path'] = path
                self.write()
                with self.assertRaises(ValueError):
                    importer.import_delivery(self.source, self.output)
        self.entry['output']['path'] = 'images/authored.png'
        self.entry['output']['sha256'] = 'a' * 64
        self.write()
        with self.assertRaises(ValueError):
            importer.import_delivery(self.source, self.output)
        self.assertFalse(self.output.exists())

    def test_unknown_game_variant_and_task_disagreement(self):
        self.entry['originals'][0]['game'] = 'other'
        self.write()
        with self.assertRaises(ValueError): importer.import_delivery(self.source, self.output)
        self.entry['originals'][0]['game'] = 'yugi'
        self.entry['originals'][0]['sha256'] = 'f' * 64
        self.write()
        with self.assertRaises(ValueError): importer.import_delivery(self.source, self.output)
        self.entry['originals'][0]['sha256'] = '0123456789abcdef' + '0' * 48
        self.entry['task_key'] = 'front:1'
        self.write()
        with self.assertRaises(ValueError): importer.import_delivery(self.source, self.output)

    def test_duplicate_identity(self):
        self.write([self.entry, self.entry])
        with self.assertRaises(ValueError): importer.import_delivery(self.source, self.output)

    def test_back_stays_explicit_back(self):
        self.entry.update(task_key='back:shared', original_card_id=None)
        self.write()
        importer.import_delivery(self.source, self.output)
        self.assertTrue(all(x['side'] == 'back' and x['cardId'] == 'shared'
                            for x in json.loads(self.output.read_text())['entries']))

    def test_duplicate_json_property(self):
        self.source.write_text('{"schema":"x","schema":"y"}')
        with self.assertRaises(ValueError): importer.import_delivery(self.source, self.output)

    def test_device_names_reject_before_image_filesystem_calls(self):
        original_stat, original_lstat = Path.stat, Path.lstat
        names = ['CON.png', 'PRN.png', 'AUX.png', 'NUL.png'] + [
            prefix + digit + suffix for prefix in ('COM', 'LPT', 'com', 'lpt')
            for digit in '123456789¹²³' for suffix in ('.png', ' .png')]
        for name in names:
            self.entry['output']['path'] = 'images/' + name
            self.write()
            def guarded_stat(path, *args, **kwargs):
                if path.name == name: raise AssertionError('Device candidate reached stat')
                return original_stat(path, *args, **kwargs)
            def guarded_lstat(path, *args, **kwargs):
                if path.name == name: raise AssertionError('Device candidate reached lstat')
                return original_lstat(path, *args, **kwargs)
            with self.subTest(name=name), mock.patch.object(Path, 'stat', guarded_stat), mock.patch.object(Path, 'lstat', guarded_lstat):
                with self.assertRaises(ValueError): importer.import_delivery(self.source, self.output)

if __name__ == '__main__':
    unittest.main()
