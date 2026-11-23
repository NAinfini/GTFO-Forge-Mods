"""Check a fresh metadata capture against a reviewed lock; never execute game code."""
from __future__ import annotations
import argparse
import copy
import json
import re
from pathlib import Path
from typing import Any


def projection(value: dict[str, Any]) -> dict[str, Any]:
    if value.get('schemaVersion') != '1.0.0' or value.get('verification') != 'metadata-only':
        raise ValueError('Capture must be schema 1.0.0 and metadata-only.')
    if value.get('purpose') != 'forge-map-native-metadata-audit' or value.get('missing') != []:
        raise ValueError('Wrong capture purpose or missing required API evidence.')
    for key, identity in [('assemblies', 'file'), ('types', 'name')]:
        rows = value[key]
        if not rows or len({row[identity] for row in rows}) != len(rows):
            raise ValueError(f'Empty or duplicate {key}.')
    digests = [value['game']['gameAssemblySha256'], value['targetsSha256']]
    digests += [row.get('signatureSha256') or row['sha256'] for row in value['assemblies']]
    if any(not re.fullmatch(r'[0-9a-f]{64}', digest) for digest in digests):
        raise ValueError('Invalid SHA-256.')
    for row in value['types']:
        signatures = [method['signature'] for method in row['methods']]
        if len(set(signatures)) != len(signatures):
            raise ValueError('Duplicate method signature.')
    return {key: value[key] for key in ['game', 'targetsSha256', 'assemblies', 'types']}

def differences(expected: Any, actual: Any, path: str = '$') -> list[str]:
    if type(expected) is not type(actual):
        return [path + ': type changed']
    if isinstance(expected, dict):
        if set(expected) != set(actual):
            return [path + ': keys changed']
        return [item for key in expected for item in differences(expected[key], actual[key], path + '.' + key)]
    if isinstance(expected, list):
        if len(expected) != len(actual):
            return [path + ': count changed']
        return [item for index, (a, b) in enumerate(zip(expected, actual))
                for item in differences(a, b, f'{path}[{index}]')]
    return [] if expected == actual else [path + ': value changed']


def verify(expected: dict[str, Any], actual: dict[str, Any]) -> list[str]:
    try:
        return differences(projection(expected), projection(actual))
    except (KeyError, TypeError, ValueError) as error:
        return ['invalid-capture: ' + str(error)]


def self_test(baseline: dict[str, Any]) -> list[str]:
    checks: list[str] = []
    if verify(baseline, copy.deepcopy(baseline)):
        raise AssertionError('Equivalent snapshot rejected.')
    checks.append('equivalent-capture')
    def check(name: str, mutate: Any) -> None:
        changed = copy.deepcopy(baseline)
        mutate(changed)
        if not verify(baseline, changed):
            raise AssertionError('Mutation was accepted: ' + name)
        checks.append(name)
    def method(value: dict[str, Any]) -> dict[str, Any]:
        return next(m for t in value['types'] for m in t['methods'] if m['parameters'])
    check('game-build', lambda x: x['game'].update(build='unsupported-build'))
    check('native-hash', lambda x: x['game'].update(gameAssemblySha256='0' * 64))
    check('assembly-signature', lambda x: next(row for row in x['assemblies'] if 'signatureSha256' in row).update(signatureSha256='0' * 64))
    check('shipped-assembly-hash', lambda x: next(row for row in x['assemblies'] if 'sha256' in row).update(sha256='0' * 64))
    check('shipped-assembly-mvid', lambda x: next(row for row in x['assemblies'] if 'mvid' in row).update(mvid='changed'))
    check('method-signature', lambda x: method(x).update(signature='void Changed()'))
    check('method-authority-visibility', lambda x: method(x).update(isPublic=not method(x)['isPublic']))
    check('parameter-out', lambda x: method(x)['parameters'][0].update(isOut=not method(x)['parameters'][0]['isOut']))
    check('parameter-optional', lambda x: method(x)['parameters'][0].update(isOptional=not method(x)['parameters'][0]['isOptional']))
    check('parameter-type', lambda x: method(x)['parameters'][0].update(type='System.String'))
    check('enum-value', lambda x: next(t for t in x['types'] if t['enumValues'])['enumValues'][0].update(value=-99999))
    check('missing-target', lambda x: x['missing'].append('RequiredType'))
    check('empty-types', lambda x: x.update(types=[]))
    check('duplicate-type', lambda x: x['types'].append(copy.deepcopy(x['types'][0])))
    check('target-contract', lambda x: x.update(targetsSha256='0' * 64))
    check('verification-escalation', lambda x: x.update(verification='game-verified'))
    check('schema-version', lambda x: x.update(schemaVersion='2.0.0'))
    return checks


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('expected', type=Path)
    parser.add_argument('actual', type=Path)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    try:
        expected = json.loads(args.expected.read_text(encoding='utf-8-sig'))
        actual = json.loads(args.actual.read_text(encoding='utf-8-sig'))
        errors = verify(expected, actual)
        checks = self_test(expected) if args.self_test and not errors else []
        print(json.dumps({'verification': 'metadata-only', 'matched': not errors,
                          'differences': errors, 'selfTests': checks,
                          'selfTestCount': len(checks)}, indent=2))
        return 1 if errors else 0
    except (OSError, ValueError, KeyError, TypeError, AssertionError) as error:
        print(json.dumps({'matched': False, 'error': str(error)}))
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
