#!/usr/bin/env bash
# Idempotent-friendly bootstrap: runs SQL files listed in manifest.order.txt in order.
# Requires: sqlcmd (Microsoft ODBC driver + mssql-tools18 on Linux, or install from docs/operations.md).
# Environment:
#   HIVEOPS_SQL_HOST   (default: localhost)
#   HIVEOPS_SQL_PORT   (default: 1433)
#   HIVEOPS_SQL_USER   (default: sa)
#   HIVEOPS_SQL_PASSWORD (required)

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SQL_HOST="${HIVEOPS_SQL_HOST:-localhost}"
SQL_PORT="${HIVEOPS_SQL_PORT:-1433}"
SQL_USER="${HIVEOPS_SQL_USER:-sa}"
SQL_PASSWORD="${HIVEOPS_SQL_PASSWORD:?Set HIVEOPS_SQL_PASSWORD}"
MANIFEST="$ROOT/db/manifest.order.txt"

if ! command -v sqlcmd >/dev/null 2>&1; then
  echo "sqlcmd is not on PATH. Install Microsoft sqlcmd (mssql-tools18) and ODBC Driver 18."
  exit 1
fi

CONN=( -S "${SQL_HOST},${SQL_PORT}" -U "$SQL_USER" -P "$SQL_PASSWORD" -C )

wait_for_sql() {
  local i
  for i in $(seq 1 60); do
    if sqlcmd "${CONN[@]}" -Q "SET NOCOUNT ON; SELECT 1" -b -h -1 >/dev/null 2>&1; then
      echo "SQL Server is reachable."
      return 0
    fi
    echo "Waiting for SQL Server (${i}/60)..."
    sleep 2
  done
  echo "SQL Server did not become ready in time."
  exit 1
}

wait_for_sql

while IFS= read -r line || [[ -n "$line" ]]; do
  [[ -z "${line// }" ]] && continue
  [[ "$line" =~ ^# ]] && continue
  script="$ROOT/db/$line"
  if [[ ! -f "$script" ]]; then
    echo "Missing script: $script"
    exit 1
  fi
  echo "Running $line ..."
  sqlcmd "${CONN[@]}" -d master -b -I -i "$script"
done < "$MANIFEST"

echo "Bootstrap finished."
