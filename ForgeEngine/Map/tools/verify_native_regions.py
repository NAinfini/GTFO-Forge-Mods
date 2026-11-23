"""Recheck prior native-region fingerprints without loading or executing GTFO."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path


def verify(manifest: dict, data: bytes) -> dict:
    actual_file = hashlib.sha256(data).hexdigest()
    rows, errors = [], []
    if actual_file != manifest['nativeSha256']:
        errors.append('game-assembly-hash-mismatch')
    names = [region['name'] for region in manifest['regions']]
    if not names or len(set(names)) != len(names):
        raise ValueError('Empty or duplicate native region names.')
    for region in manifest['regions']:
        offset, length = region['fileOffset'], region['length']
        if type(offset) is not int or type(length) is not int or offset < 0 or length <= 0 or offset + length > len(data):
            raise ValueError('Invalid region range: ' + region['name'])
        actual = hashlib.sha256(data[offset:offset + length]).hexdigest()
        matched = actual == region['sha256']
        rows.append({'name': region['name'], 'rva': region['rva'], 'fileOffset': offset,
                     'length': length, 'sha256': actual, 'matched': matched})
        if not matched:
            errors.append('region-hash-mismatch:' + region['name'])
    return {'verification': 'static-file-fingerprints-only', 'gameAssemblySha256': actual_file,
            'matched': not errors, 'regions': rows, 'errors': errors,
            'limits': ['Reuses prior region boundaries; does not re-disassemble',
                       'Does not prove native invocation, callback order or multiplayer behavior']}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('baseline', type=Path)
    parser.add_argument('game_assembly', type=Path)
    args = parser.parse_args()
    try:
        raw = args.baseline.read_bytes()
        manifest = json.loads(raw.decode('utf-8-sig'))
        result = verify(manifest, args.game_assembly.read_bytes())
        result['baselineManifestSha256'] = hashlib.sha256(raw).hexdigest()
        print(json.dumps(result, indent=2))
        return 0 if result['matched'] else 1
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(json.dumps({'matched': False, 'error': str(error)}))
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
