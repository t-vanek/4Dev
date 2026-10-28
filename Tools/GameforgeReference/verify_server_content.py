"""Verify every old row of affected charts is retained and only the prepared server rows were added."""
import argparse
import collections
import datetime
import hashlib
import json
import math
from pathlib import Path
import re

from sql_snapshot import sql


def canonical(row):
    return json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(',', ':'))


def same(a, b):
    if isinstance(a, float) or isinstance(b, float):
        return a is not None and b is not None and math.isclose(a, b, rel_tol=1e-14, abs_tol=1e-10)
    return a == b


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--plan', type=Path, required=True)
    parser.add_argument('--database', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.plan.read_text(encoding='utf-8'))
    checked = []
    for table, additions in report['additions'].items():
        if not re.fullmatch(r'[A-Za-z0-9_]+', table):
            raise ValueError('Invalid table name')
        before = json.loads((args.snapshot / (table + '.json')).read_text(encoding='utf-8'))
        after = sql('SELECT * FROM dbo.[' + table + '] FOR JSON PATH, INCLUDE_NULL_VALUES;', args.database)
        old = collections.Counter(map(canonical, before))
        actual = collections.Counter(map(canonical, after))
        if old - actual:
            raise ValueError('A pre-existing row changed: ' + table)
        if len(after) != len(before) + len(additions):
            raise ValueError('Unexpected added row count: ' + table)
        new = [json.loads(row) for row, count in (actual - old).items() for _ in range(count)]
        for expected in additions:
            matching = next((i for i, row in enumerate(new)
                             if row.keys() == expected.keys() and all(same(row[k], v) for k, v in expected.items())), None)
            if matching is None:
                raise ValueError('Missing/changed planned row: ' + table + ' ' + canonical(expected))
            new.pop(matching)
        if new:
            raise ValueError('Unplanned new rows in ' + table)
        checked.append({'table': table, 'old_rows_preserved': len(before), 'added_rows': len(additions)})
    # New references must resolve in the actual database, rather than only looking valid in a plan.
    failures = sql("""
SELECT 'recipe-giver' AS kind, q.dwQuestID AS id FROM TQUESTCHART q LEFT JOIN TNPCCHART n ON n.wID=q.dwTriggerID
WHERE q.bType=18 AND q.dwQuestID IN(""" + ','.join(str(r['dwQuestID']) for r in report['additions'].get('TQUESTCHART', [])) + """) AND (n.wID IS NULL OR n.bType<>20)
FOR JSON PATH;
""", args.database) if report['additions'].get('TQUESTCHART') else []
    if failures:
        raise ValueError('Unresolved runtime references: ' + str(failures))
    schema_before = json.loads((args.snapshot / 'schema.json').read_text(encoding='utf-8'))
    label_schema = sql("""
SELECT t.name AS table_name,c.name AS column_name,ty.name AS data_type,c.max_length,c.is_nullable,
       dc.definition AS default_sql FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id
JOIN sys.types ty ON ty.user_type_id=c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
WHERE t.name IN('TNPCCHART','TMONSTERCHART') AND c.name IN('szName','szName2','NC_szName2')
FOR JSON PATH, INCLUDE_NULL_VALUES;
""", args.database)
    for row in label_schema:
        previous = next(c for c in schema_before if c['table_name'] == row['table_name'] and c['column_name'] == row['column_name'])
        if row['data_type'] != 'nvarchar' or row['max_length'] != previous['max_length'] * 2 or row['is_nullable'] != previous['is_nullable'] or row['default_sql'] != previous.get('default_sql'):
            raise ValueError('Unicode conversion changed a column default or constraint: ' + str(row))
    result = {'database': args.database, 'passed': True,
              'checked_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'plan_sha256': hashlib.sha256(args.plan.read_bytes()).hexdigest(), 'tables': checked,
              'old_chart_rows_preserved': sum(r['old_rows_preserved'] for r in checked),
              'added_rows': sum(r['added_rows'] for r in checked), 'unicode_label_defaults_preserved': True}
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k != 'tables'}))


if __name__ == '__main__':
    main()
