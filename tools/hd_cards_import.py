"""Create an HD Cards manifest from a local reconstruction delivery; copy no artwork."""
import argparse
import hashlib
import json
import os
import re
import stat
import struct
import sys
from pathlib import Path

SCHEMA = 'power-of-chaos-hd-cards-v1'

def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate JSON property: ' + key)
        result[key] = value
    return result

def refuse_links(path):
    for part in (path, *path.parents):
        if part.is_symlink() or (part.exists() and getattr(part.lstat(), 'st_file_attributes', 0)
                                  & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 1024)):
            raise ValueError('Directory links are not supported: ' + str(part))

def import_delivery(delivery, output):
    delivery = Path(os.path.abspath(delivery))
    output = Path(os.path.abspath(output))
    refuse_links(delivery)
    refuse_links(output)
    if output.is_relative_to(delivery.parent):
        raise ValueError('Write the new manifest outside the read-only delivery directory')
    if delivery.stat().st_size > 16 * 1024 * 1024:
        raise ValueError('Delivery manifest exceeds 16 MiB')
    data = json.loads(delivery.read_text(encoding='utf-8-sig'), object_pairs_hook=unique_object)
    if data.get('schema') != 'power-of-chaos-reconstructed-card-delivery-v1':
        raise ValueError('Unrecognized reconstruction delivery schema')
    entries = data.get('entries')
    if not isinstance(entries, list) or not 1 <= len(entries) <= 4096:
        raise ValueError('Delivery must have 1..4096 renditions')
    asset_root = delivery.parent / 'images'
    refuse_links(asset_root)
    if not asset_root.is_dir():
        raise ValueError('Delivery images directory is missing')
    emitted = []
    identities = set()
    images_checked = 0
    total_bytes = 0
    for entry in entries:
        if entry.get('state') != 'accepted':
            raise ValueError('Unaccepted rendition in delivery')
        task = entry.get('task_key', '')
        original_id = entry.get('original_card_id')
        if task == 'back:shared' and original_id is None:
            card_id, side = 'shared', 'back'
        elif isinstance(original_id, int) and not isinstance(original_id, bool) and 0 <= original_id < 100000:
            if task != 'front:' + str(original_id):
                raise ValueError('Front task and explicit original card ID disagree')
            card_id, side = str(original_id), 'front'
        else:
            raise ValueError('Missing explicit front/back identity')
        art_id = entry.get('variant_key', '')
        if not isinstance(art_id, str) or not re.fullmatch('[0-9a-f]{16}', art_id):
            raise ValueError('Invalid explicit art variant')
        item = entry['output']
        relative = item['path']
        if not isinstance(relative, str) or '\\' in relative:
            raise ValueError('Expected a portable image path')
        parts = relative.split('/')
        if len(parts) != 2 or parts[0] != 'images' or parts[1] in ('', '.', '..'):
            raise ValueError('Expected one file directly inside delivery/images')
        name = parts[1]
        if any(ord(c) < 32 or c in ':<>"|?*' for c in name) or name != name.rstrip('. '):
            raise ValueError('Invalid image filename')
        stem = name.split('.')[0].rstrip(' ').upper()
        if stem in ('CON', 'PRN', 'AUX', 'NUL') or (
                len(stem) == 4 and stem[:3] in ('COM', 'LPT') and stem[3] in '123456789¹²³'):
            raise ValueError('Windows device names are not image files')
        image = asset_root / name
        refuse_links(image)
        if image.suffix.lower() != '.png' or not image.is_file():
            raise ValueError('Missing PNG rendition')
        size = image.stat().st_size
        if not 45 <= size <= 16 * 1024 * 1024 or size != item['bytes']:
            raise ValueError('Unexpected rendition size')
        expected = item['sha256']
        if not isinstance(expected, str) or not re.fullmatch('[0-9a-f]{64}', expected):
            raise ValueError('Invalid rendition hash')
        with image.open('rb') as stream:
            header = stream.read(33)
            stream.seek(0)
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        if digest != expected:
            raise ValueError('Rendition hash mismatch: ' + name)
        if header[:16] != b'\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR' or struct.unpack('>IIBBBBB', header[16:29]) != (800, 1160, 8, 2, 0, 0, 0):
            raise ValueError('Expected noninterlaced 800 by 1160 RGB8 PNG: ' + name)
        if item.get('size') != [800, 1160]:
            raise ValueError('Delivery dimensions disagree')
        originals = entry.get('originals')
        if not isinstance(originals, list) or not originals:
            raise ValueError('Missing explicit per-game mapping')
        games = set()
        for original in originals:
            game = original.get('game')
            if game not in ('yugi', 'kaiba', 'joey'):
                raise ValueError('Unrecognized game mapping')
            original_hash = original.get('sha256', '')
            if not isinstance(original_hash, str) or not re.fullmatch('[0-9a-f]{64}', original_hash) or not original_hash.startswith(art_id):
                raise ValueError('Art variant and original art hash disagree')
            games.add(game)
        for game in sorted(games):
            identity = (game, card_id, art_id, side)
            if identity in identities:
                raise ValueError('Duplicate explicit card/art/game identity')
            identities.add(identity)
            emitted.append(dict(game=game, cardId=card_id, artId=art_id, side=side,
                                path=name, sha256=expected))
        images_checked += 1
        total_bytes += size
    manifest = dict(schema=SCHEMA,
                    dimensions=dict(physicalWidth=800, physicalHeight=1160, logicalWidth=200, logicalHeight=290),
                    variantPolicy='explicit-art-id-only', entries=emitted)
    # Exclusive creation: no existing manifest, image or historical delivery is replaced.
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(manifest, stream, indent=2)
        stream.write('\n')
    return dict(imagesChecked=images_checked, imageBytes=total_bytes, mappings=len(emitted),
                manifest=str(output), assetsRoot=str(asset_root),
                scope='Local pack import and hash/header checks; not full PNG decode or game integration')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--delivery', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        result = import_delivery(args.delivery, args.output)
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(1, 'HD import failed: ' + str(error) + '\n')
    print(json.dumps(result))

if __name__ == '__main__':
    main()
