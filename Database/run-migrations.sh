#!/bin/bash
# Restore baselines (once), then apply Database/migrations/*.sql in lexicographic order, exactly once each.
#
# Applied migrations are tracked in TGlobal_gsp.dbo._SchemaMigrations (keyed by file name), so re-runs
# are idempotent. Each migration file selects its own database with `USE [TGlobal_gsp]` / `USE [TGame_gsp]`.
# A migration that fails aborts the importer (and the loginsvr that depends on it) without recording it.
set -euo pipefail

SQLCMD=/opt/mssql-tools18/bin/sqlcmd
HOST="${SQL_HOST:-mssql}"
PW="${SA_PASSWORD:?SA_PASSWORD must be set}"

# sqlcmd helper: -C trust cert, -b return error code on SQL error.
sql() { "$SQLCMD" -S "$HOST" -U sa -P "$PW" -C -b "$@"; }

echo "Waiting for SQL Server at $HOST..."
for i in $(seq 1 60); do
  if sql -Q "SELECT 1" >/dev/null 2>&1; then echo "SQL Server is up."; break; fi
  echo "  waiting ($i)..."; sleep 2
  if [ "$i" -eq 60 ]; then echo "SQL Server did not become ready." >&2; exit 1; fi
done

echo "== Restore (idempotent) =="
sql -i /Database/restore.sql

# The server answers SELECT 1 before user databases finish recovery (e.g. right after an mssql restart),
# so `-d TGlobal_gsp` would fail with "Cannot open database". Wait for both to come ONLINE.
echo "Waiting for databases to come online..."
for i in $(seq 1 60); do
  online=$(sql -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name IN (N'TGlobal_gsp', N'TGame_gsp') AND state_desc = N'ONLINE';" 2>/dev/null | tr -d '[:space:]' || true)
  if [ "$online" = "2" ]; then echo "Databases are online."; break; fi
  echo "  waiting ($i)..."; sleep 2
  if [ "$i" -eq 60 ]; then echo "Databases did not come online." >&2; exit 1; fi
done

echo "== Migration tracking table =="
sql -d TGlobal_gsp -Q "IF OBJECT_ID('dbo._SchemaMigrations') IS NULL CREATE TABLE dbo._SchemaMigrations (Id NVARCHAR(260) NOT NULL PRIMARY KEY, AppliedUtc DATETIME2 NOT NULL CONSTRAINT DF_SchemaMigrations_AppliedUtc DEFAULT SYSUTCDATETIME());"

echo "== Migrations =="
shopt -s nullglob
applied_any=0
# Bash expands the glob in sorted order; nullglob makes it expand to nothing when there are no files.
for f in /Database/migrations/*.sql; do
  name=$(basename "$f")
  cnt=$(sql -d TGlobal_gsp -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo._SchemaMigrations WHERE Id=N'$name';" | tr -d '[:space:]')
  if [ "$cnt" = "0" ]; then
    echo "  applying $name"
    # -v SA_PASSWORD=... lets a migration reference the .env SA password as '$(SA_PASSWORD)'
    # instead of hardcoding the secret in the committed .sql file.
    sql -v SA_PASSWORD="$PW" -i "$f"
    sql -d TGlobal_gsp -Q "INSERT INTO dbo._SchemaMigrations (Id) VALUES (N'$name');"
    applied_any=1
  else
    echo "  skip $name (already applied)"
  fi
done
if [ "$applied_any" -eq 0 ]; then echo "  (no pending migrations)"; fi

if [ -n "${LOCAL_TOPOLOGY_FILE:-}" ]; then
  echo "== Local client topology =="
  sql -i "$LOCAL_TOPOLOGY_FILE"
fi

echo "Import + migrations complete."
