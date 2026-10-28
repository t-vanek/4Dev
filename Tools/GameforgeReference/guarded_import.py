"""Generate additive, idempotent SQL with complete-row conflict guards."""
from normalization import sql_type, sql_value


def additions_sql(additions, schema, plan_path, key_overrides=None):
    lines = ['-- Audited sources and reconstruction policies: ' + plan_path,
             'USE [TGame_gsp];', 'GO', 'SET QUOTED_IDENTIFIER ON;', 'SET ANSI_NULLS ON;',
             'SET NOCOUNT ON;', 'SET XACT_ABORT ON;', 'BEGIN TRANSACTION;']
    for table, rows in additions.items():
        if not rows:
            continue
        cols = [c for c in schema if c['table_name'] == table]
        keys = [c['column_name'] for c in sorted(cols, key=lambda c: c.get('primary_key_ordinal') or 999)
                if c.get('primary_key_ordinal')]
        keys = (key_overrides or {}).get(table, keys)
        if not keys or any(c['is_identity'] or c['is_computed'] for c in cols):
            raise ValueError('Explicit stable import key required: ' + table)
        if any(set(r) != {c['column_name'] for c in cols} for r in rows):
            raise ValueError('Incomplete import row: ' + table)
        names = ','.join('[' + c['column_name'] + ']' for c in cols)
        temp = '#Gf_' + table
        definition = ','.join('[' + c['column_name'] + '] ' + sql_type(c) +
                              (' NULL' if c['is_nullable'] else ' NOT NULL') for c in cols)
        lines.append(f'CREATE TABLE {temp}({definition});')
        values = ['(' + ','.join(sql_value(r[c['column_name']], c['data_type']) for c in cols) + ')' for r in rows]
        for start in range(0, len(values), 250):
            lines.append(f'INSERT {temp} ({names}) VALUES ' + ',\n'.join(values[start:start+250]) + ';')
        join = ' AND '.join(f't.[{k}]=n.[{k}]' for k in keys)
        left, right = (','.join(prefix + '.[' + c['column_name'] + ']' for c in cols) for prefix in ('t', 'n'))
        lines += [f"IF EXISTS(SELECT 1 FROM {temp} n JOIN dbo.[{table}] t ON {join} WHERE EXISTS(SELECT {left} EXCEPT SELECT {right})) THROW 51000,'Conflicting existing {table} key',1;",
                  f'INSERT dbo.[{table}] ({names}) SELECT {right} FROM {temp} n WHERE NOT EXISTS(SELECT 1 FROM dbo.[{table}] t WHERE {join});',
                  f"IF EXISTS(SELECT {names} FROM {temp} EXCEPT SELECT {names} FROM dbo.[{table}]) THROW 51000,'Missing or changed {table} row',1;",
                  f'DROP TABLE {temp};']
    return '\n'.join(lines + ['COMMIT TRANSACTION;', 'GO']) + '\n'
