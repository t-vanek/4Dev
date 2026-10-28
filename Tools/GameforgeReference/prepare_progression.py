"""Prepare guarded Gameforge CZ EXP thresholds from the publisher team's numeric table."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path

from client_facts import fixed
from normalization import migration, sql_value


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('snapshot', 'source', 'image', 'output', 'sql'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    published = read(Path('Gameforge/progression-140/publisher-exp.json'))
    if hashlib.sha256(args.image.read_bytes()).hexdigest() != published['image_sha256']:
        raise ValueError('Changed publisher EXP image')
    rows = published['rows']
    if [r[0] for r in rows] != list(range(89, 141)):
        raise ValueError('Incomplete consecutive publisher transcription')
    for previous, current in zip(rows, rows[1:]):
        if current[1] - previous[1] != current[2] or current[2] <= 0:
            raise ValueError('Transcription columns disagree at level ' + str(current[0]))
    incoming = {level - 1: exp for level, exp, _ in rows}
    # Zero marks the closed ceiling at maximum level; it is not a published level-141 threshold.
    incoming[140] = 0
    baseline = {r['bLevel']: r for r in read(args.snapshot / 'TLEVELCHART.json')}
    if set(baseline) != set(range(126)):
        raise ValueError('Use the immutable pre-035 snapshot, not an already-expanded live chart')
    if any(baseline[n]['dwEXP'] != incoming[n] for n in range(88, 99)):
        raise ValueError('Publisher/SQL offset validation failed')
    fees_path = args.source / 'Tcd/TLevel.tcd'
    fees_hash = hashlib.sha256(fees_path.read_bytes()).hexdigest()
    manifest = read(Path('Gameforge/reference.json'))
    if fees_hash != next(r['sha256'] for r in manifest['files'] if r['path'].lower() == 'tcd/tlevel.tcd'):
        raise ValueError('Changed original fee chart')
    fees = {r['bLevel']: r for r in fixed(fees_path.read_bytes(), 'BIII', ('bLevel', 'dwMoney', 'dwRegCost', 'dwSearchCost'))}
    changes = []
    for level in range(99, 126):
        if baseline[level]['dwEXP'] != incoming[level]:
            changes.append({'table': 'TLEVELCHART', 'key_column': 'bLevel', 'key': level,
                            'column': 'dwEXP', 'before': baseline[level]['dwEXP'], 'after': incoming[level],
                            'source': published['source']})
    additions = []
    for level in range(126, 141):
        additions.append({**baseline[125], **fees[level], 'dwEXP': incoming[level]})
    plan = {'prepared_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'publisher_source': published, 'fee_source_sha256': fees_hash,
            'index_semantics': 'TLEVELCHART[L].dwEXP is cumulative EXP required to reach L+1.',
            'validated_overlap_current_levels': list(range(88, 99)),
            'player_level_cap': 140, 'cap_row_policy': 'Row 140 EXP=0 closes progression; no invented level-141 threshold.',
            'changes': changes, 'additions': {'TLEVELCHART': additions},
            'compatibility_fields_not_certified_gameforge': {
                'donor_level': 125,
                'fields': sorted(set(baseline[125]) - set(fees[125]) - {'dwEXP'}),
                'policy': 'New non-EXP/non-client-fee columns retain the last existing level row. This does not certify skill-point, HP/MP or PvP curves. Existing rows and saved characters are not rewritten except the listed EXP thresholds.'},
            'saved_character_policy': 'No saved level or cumulative EXP is changed by this migration; progression is evaluated on the next positive EXP gain.'}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(plan, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    schema = read(args.snapshot / 'schema.json')
    migration(plan, schema, args.sql)
    columns = [c for c in schema if c['table_name'] == 'TLEVELCHART']
    names = ','.join('[' + c['column_name'] + ']' for c in columns)
    statements = []
    for row in additions:
        values = ','.join(sql_value(row[c['column_name']], c['data_type']) for c in columns)
        statements += [f"IF EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel={row['bLevel']} AND EXISTS(SELECT {names} EXCEPT SELECT {values})) THROW 51000,'Conflicting high-level chart row',1;",
                       f"IF NOT EXISTS(SELECT 1 FROM dbo.TLEVELCHART WHERE bLevel={row['bLevel']}) INSERT dbo.TLEVELCHART ({names}) VALUES ({values});"]
    script = args.sql.read_text(encoding='utf-8')
    script = '-- Plan and reconstruction boundaries: Gameforge/progression-140/plan.json\n' + script
    script = script.replace('COMMIT TRANSACTION;', '\n'.join(statements) + '\nCOMMIT TRANSACTION;')
    args.sql.write_text(script, encoding='utf-8')
    print(json.dumps({'corrected_existing_thresholds': len(changes), 'new_level_rows': len(additions),
                      'exp_to_reach_126': incoming[125], 'exp_to_reach_140': incoming[139]}))


if __name__ == '__main__':
    main()
