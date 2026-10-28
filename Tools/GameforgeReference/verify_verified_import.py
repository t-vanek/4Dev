"""Compare all affected rows with the frozen baseline plus only the explicit import."""
import argparse
import collections
import datetime
import hashlib
import json
from pathlib import Path
import struct

from sql_snapshot import sql


def load(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--database', required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    path = Path('Gameforge/server-completion/plan.json')
    plan = load(path)
    base = Path(plan['baseline'])
    for name, expected in plan['baseline_sha256'].items():
        if hashlib.sha256((base / name).read_bytes()).hexdigest() != expected:
            raise ValueError('Frozen baseline changed: ' + name)
    names = {'TMONSTERCHART', 'TMONATTRCHART', 'TGAMEFORGEMONSTERRESTORE', 'TNPCCHART', 'TNPCITEMCHART'}
    before = {t: load(base / (t + '.json')) for t in names}
    expected = json.loads(json.dumps(before))
    for c in plan['changes']:
        row = next(r for r in expected[c['table']] if r[c['key_column']] == c['key'])
        if row[c['column']] != c['before']:
            raise ValueError('Plan differs from frozen baseline')
        row[c['column']] = c['after']
    for r in plan['monster_restores']:
        monster = next(x for x in expected['TMONSTERCHART'] if x['wID'] == r['id'])
        monster.update(bLevel=r['level'], wMonAttr=r['new_attr'])
        donor = dict(next(x for x in expected['TMONATTRCHART'] if (x['wID'], x['bLevel']) == (r['old_attr'], r['old_level'])))
        donor.update(wID=r['new_attr'], bLevel=r['level'], dwMaxHP=r['hp'])
        expected['TMONATTRCHART'].append(donor)
    for t, rows in plan['additions'].items():
        expected[t] += rows
    actual = {t: sql(f'SELECT * FROM dbo.[{t}] FOR JSON PATH,INCLUDE_NULL_VALUES;', a.database) for t in sorted(names)}
    old_audit = {r['wMonID'] for r in before['TGAMEFORGEMONSTERRESTORE']}
    new_audit = [r for r in actual['TGAMEFORGEMONSTERRESTORE'] if r['wMonID'] not in old_audit]
    if {r['wMonID'] for r in new_audit} != {r['id'] for r in plan['monster_restores']}:
        raise ValueError('Unexpected restore audit IDs')
    for r in plan['monster_restores']:
        row = next(x for x in new_audit if x['wMonID'] == r['id'])
        target = {'wMonID': r['id'], 'bOriginalLevel': r['old_level'], 'wOriginalAttr': r['old_attr'],
                  'dwOriginalHP': r['old_hp'], 'wReferenceAttr': r['new_attr'], 'bReferenceLevel': r['level'],
                  'dwReferenceHP': r['hp'], 'szSource': r['source'], 'szSourceSha256': r['source_sha256']}
        if not row.get('tApplied') or {k: v for k, v in row.items() if k != 'tApplied'} != target:
            raise ValueError('Unexpected restore audit fields')
        expected['TGAMEFORGEMONSTERRESTORE'].append(row)
    schema = load(base / 'schema.json')
    checks = []
    for table in sorted(names):
        floats = {c['column_name'] for c in schema if c['table_name'] == table and c['data_type'] in ('real', 'float')}

        def canonical(rows):
            return collections.Counter(json.dumps({k: struct.pack('<f', v).hex() if k in floats and v is not None else v
                                                    for k, v in row.items()}, sort_keys=True) for row in rows)
        if canonical(expected[table]) != canonical(actual[table]):
            raise ValueError('Unplanned full-row difference: ' + table)
        checks.append({'table': table, 'before_rows': len(before[table]), 'after_rows': len(actual[table]), 'passed': True})
    result = {'database': a.database, 'verified_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'passed': True, 'plan_sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'tables': checks,
              'mapped_fields': len(plan['changes']), 'singleton_level_hp_restores': len(plan['monster_restores']),
              'old_rows_checked': sum(len(v) for v in before.values()), 'saved_character_rows_written': 0}
    a.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
