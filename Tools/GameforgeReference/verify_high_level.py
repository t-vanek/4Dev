"""Check complete changed chart contents against the frozen baseline and the two explicit plans."""
import argparse
import collections
import hashlib
import json
from pathlib import Path

from normalization import equal
from sql_snapshot import sql


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', required=True)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    files = [Path('Gameforge/progression-140/plan.json'), Path('Gameforge/high-level-regions/plan.json')]
    plans = [load(p) for p in files]
    additions = collections.defaultdict(list)
    for plan in plans:
        for name, rows in plan['additions'].items():
            additions[name] += rows
    changes = plans[0]['changes']
    schema = load(args.snapshot / 'schema.json')
    checks = []
    for table in sorted(additions):
        before = load(args.snapshot / (table + '.json'))
        expected = [dict(r) for r in before]
        for change in changes:
            if change['table'] == table:
                row = next(r for r in expected if r[change['key_column']] == change['key'])
                if row[change['column']] != change['before']:
                    raise ValueError('Changed frozen baseline')
                row[change['column']] = change['after']
        expected += additions[table]
        actual = sql(f'SELECT * FROM dbo.[{table}] FOR JSON PATH,INCLUDE_NULL_VALUES;', args.database)
        cols = [c for c in schema if c['table_name'] == table]
        keys = [c['column_name'] for c in sorted(cols, key=lambda c: c.get('primary_key_ordinal') or 999)
                if c.get('primary_key_ordinal')]
        if not keys:
            keys = {'TNPCITEMCHART': ['wNpcID', 'dwItemID'],
                    'TCHANNELCHART': ['bGroupID', 'wMapID', 'wUnitID', 'bLogChannel']}[table]
        def key(r):
            return tuple(r[k] for k in keys)
        expected.sort(key=key)
        actual.sort(key=key)
        if len(expected) != len(actual) or any(set(a) != set(b) or any(not equal(a[k], b[k]) for k in a)
                                              for a, b in zip(expected, actual)):
            raise ValueError('Unexpected chart content: ' + table)
        checks.append({'table': table, 'old_rows': len(before), 'added_rows': len(additions[table]), 'final_rows': len(actual),
                       'expected_sha256': hashlib.sha256(json.dumps(expected, sort_keys=True, separators=(',', ':')).encode()).hexdigest()})
    result = {'database': args.database, 'passed': True, 'tables': checks,
              'plans': [{'path': str(p), 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in files],
              'changed_old_exp_thresholds': len(changes), 'saved_character_rows_written': 0}
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'database': args.database, 'passed': True, 'tables': len(checks),
                      'old_rows_checked': sum(r['old_rows'] for r in checks), 'added_rows': sum(r['added_rows'] for r in checks)}))


if __name__ == '__main__':
    main()
