"""Read chart tables and schema from the local SQL container without exposing its credentials."""
import argparse
import json
from pathlib import Path
import re
import subprocess


def sql(query, database="TGame_gsp"):
    if not re.fullmatch(r"[A-Za-z0-9_]+", database):
        raise ValueError("Invalid database identifier")
    command = ["docker", "exec", "-i", "araz-mssql", "/bin/bash", "-c",
               'exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -y 0 -w 65535 "$@"',
               "sqlcmd", "-d", database]
    result = subprocess.run(command, input="SET NOCOUNT ON;\n" + query, capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or result.stdout.strip())
    return json.loads("".join(result.stdout.splitlines()).strip() or "[]")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--tables", nargs="*")
    parser.add_argument("--database", default="TGame_gsp")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    schema = sql("""
SELECT t.name AS table_name,c.name AS column_name,c.column_id,ty.name AS data_type,c.max_length,c.precision,c.scale,
       c.is_nullable,c.is_identity,c.is_computed,dc.definition AS default_sql,
       (SELECT TOP(1) ic.key_ordinal FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
        WHERE i.object_id=t.object_id AND i.is_primary_key=1 AND ic.column_id=c.column_id) AS primary_key_ordinal
FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
ORDER BY t.name,c.column_id FOR JSON PATH;
""",args.database)
    counts = sql("""
SELECT t.name AS table_name,SUM(p.rows) AS row_count FROM sys.tables t JOIN sys.partitions p ON p.object_id=t.object_id
WHERE p.index_id IN(0,1) GROUP BY t.name ORDER BY t.name FOR JSON PATH;
""",args.database)
    (args.output / "schema.json").write_text(json.dumps(schema, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (args.output / "counts.json").write_text(json.dumps(counts, indent=2) + "\n", encoding="utf-8")
    available = {row["table_name"] for row in counts}
    tables = args.tables or sorted(name for name in available if re.search(r"CHART\d*$", name) or name.startswith("TSTART"))
    for table in tables:
        if table not in available or not re.fullmatch(r"[A-Za-z0-9_]+", table):
            raise ValueError(f"Unknown chart table: {table}")
        columns = [column for column in schema if column["table_name"] == table]
        key = sorted((column for column in columns if column.get("primary_key_ordinal")), key=lambda column: column["primary_key_ordinal"])
        order = " ORDER BY " + ",".join("[" + column["column_name"] + "]" for column in key) if key else ""
        rows = sql("SELECT * FROM dbo.[" + table + "]" + order + " FOR JSON PATH, INCLUDE_NULL_VALUES;",args.database)
        (args.output / (table + ".json")).write_text(json.dumps(rows, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
        print(f"Read {table}: {len(rows)} rows", flush=True)
    print(f"Saved schema for {len(available)} tables and {len(tables)} chart snapshots: {args.output}")


if __name__ == "__main__":
    main()
