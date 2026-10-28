"""Synthetic schema compatibility and unchanged rejection gates; no real artwork."""
import copy
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

P = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('fixtures', P / 'test_hd_cards_visible_pack.py')
fixtures = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixtures)
pack = fixtures.pack


class SerifDeliveryTests(unittest.TestCase):
    def setUp(self):
        scratch = P.parent / '.local/hd-packaging-check/serif-tests'
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.delivery = self.root / 'delivery'
        (self.delivery / 'images').mkdir(parents=True)
        self.original = fixtures.png(200, 290, bytes([255, 0, 0]) * 200 * 290)
        self.master = fixtures.png(800, 1160, bytes([0, 255, 0]) * 800 * 1160)
        (self.root / 'original.png').write_bytes(self.original)
        (self.delivery / 'images/master.png').write_bytes(self.master)
        self.entry = {
            'state': 'accepted', 'original_card_id': 0, 'variant_key': 'authored-art',
            'output': {'path': 'images/master.png', 'sha256': hashlib.sha256(self.master).hexdigest()},
            'originals': [{'game': g, 'path': 'original.png',
                           'sha256': hashlib.sha256(self.original).hexdigest()} for g in pack.GAMES],
        }

    def build(self, schema, entries=None, suffix='out'):
        manifest = self.delivery / 'manifest.json'
        manifest.write_text(json.dumps({'schema': schema, 'entries': entries or [self.entry]}), encoding='utf-8')
        return pack.build(manifest, self.root, self.root / suffix, list(pack.GAMES), [1, 2])

    def test_exact_old_and_serif_schema_mapping_parity(self):
        receipts = []
        for number, schema in enumerate(sorted(pack.DELIVERY_SCHEMAS)):
            out = 'out' + str(number)
            result = self.build(schema, suffix=out)
            self.assertEqual((6, 2, 0, 0), (result['mappings'], result['referenceFiles'],
                                          result['skipped'], result['ambiguousVisibleGroups']))
            receipt = json.loads((self.root / out / 'pack-receipt.json').read_text())
            for row in receipt['records']:
                self.assertEqual(0, row['cardId'])
                self.assertEqual('authored-art', row['artId'])
                self.assertEqual(116000, Path(row['reference']).stat().st_size)
                self.assertEqual(hashlib.sha256(Path(row['reference']).read_bytes()).hexdigest(),
                                 row['referenceSha256'])
            receipts.append([{k: v for k, v in row.items() if k != 'reference'}
                             for row in receipt['records']])
        self.assertEqual(receipts[0], receipts[1])
        self.assertEqual(self.original, (self.root / 'original.png').read_bytes())
        self.assertEqual(self.master, (self.delivery / 'images/master.png').read_bytes())

    def test_unknown_and_nearby_schemas_rejected(self):
        for number, schema in enumerate(['unknown', 'power-of-chaos-complete-collection-serif-v2-manifest-v2',
                                         'power-of-chaos-complete-collection-serif-v2-manifest-v1-extra']):
            with self.subTest(schema=schema), self.assertRaises(ValueError):
                self.build(schema, suffix='bad' + str(number))
            self.assertFalse((self.root / ('bad' + str(number))).exists())

    def test_both_schemas_reject_unaccepted_and_hash_mismatches(self):
        cases = []
        rejected = copy.deepcopy(self.entry)
        rejected['state'] = 'pending'
        cases.append(rejected)
        bad_master = copy.deepcopy(self.entry)
        bad_master['output']['sha256'] = '0' * 64
        cases.append(bad_master)
        bad_original = copy.deepcopy(self.entry)
        bad_original['originals'][0]['sha256'] = '0' * 64
        cases.append(bad_original)
        for s, schema in enumerate(sorted(pack.DELIVERY_SCHEMAS)):
            for n, entry in enumerate(cases):
                with self.subTest(schema=schema, case=n), self.assertRaises(ValueError):
                    self.build(schema, [entry], 'bad-%s-%s' % (s, n))

    def test_serif_rejects_original_and_master_path_escape(self):
        schema = 'power-of-chaos-complete-collection-serif-v2-manifest-v1'
        for number, name in enumerate(['../original.png', 'a//original.png', 'NUL.png', 'C:/original.png']):
            for field in ['originals', 'output']:
                entry = copy.deepcopy(self.entry)
                if field == 'originals':
                    entry[field][0]['path'] = name
                else:
                    entry[field]['path'] = name
                with self.subTest(field=field, path=name), self.assertRaises(ValueError):
                    self.build(schema, [entry], 'bad-%s-%s' % (field, number))

    def test_serif_preserves_explicit_199_wide_exclusion_and_null_back_id(self):
        narrow = fixtures.png(199, 290, bytes([0, 0, 255]) * 199 * 290)
        (self.root / 'narrow.png').write_bytes(narrow)
        entry = copy.deepcopy(self.entry)
        entry['original_card_id'] = None
        entry['variant_key'] = 'authored-back'
        entry['originals'].append({'game': 'yugi', 'path': 'narrow.png',
                                   'sha256': hashlib.sha256(narrow).hexdigest()})
        result = self.build('power-of-chaos-complete-collection-serif-v2-manifest-v1', [entry])
        self.assertEqual(6, result['mappings'])
        self.assertEqual(1, result['skipped'])
        receipt = json.loads((self.root / 'out/pack-receipt.json').read_text())
        self.assertEqual([199, 290], receipt['skipped'][0]['dimensions'])
        self.assertTrue(all(row['cardId'] is None and row['artId'] == 'authored-back'
                            for row in receipt['records']))


if __name__ == '__main__':
    unittest.main()
