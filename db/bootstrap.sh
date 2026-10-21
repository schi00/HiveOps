#!/usr/bin/env bash
# Idempotent bootstrap for HiveOps on PostgreSQL.
# Runs db/bootstrap.postgresql.sql (consolidated schema + RLS + seeds).
#
# Requires: psql (PostgreSQL client).
# Environment:
#   HIVEOPS_PG_HOST     (default: localhost)
#   HIVEOPS_PG_PORT     (default: 5432)
#   HIVEOPS_PG_USER     (default: postgres)
#   HIVEOPS_PG_DB       (default: hive)
#   HIVEOPS_PG_PASSWORD (required, exported as PGPASSWORD)

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PG_HOST="${HIVEOPS_PG_HOST:-localhost}"
PG_PORT="${HIVEOPS_PG_PORT:-5432}"
PG_USER="${HIVEOPS_PG_USER:-postgres}"
PG_DB="${HIVEOPS_PG_DB:-hive}"
export PGPASSWORD="${HIVEOPS_PG_PASSWORD:?Set HIVEOPS_PG_PASSWORD}"
SCRIPT="$ROOT/db/bootstrap.postgresql.sql"

if ! command -v psql >/dev/null 2>&1; then
  echo "psql is not on PATH. Install the PostgreSQL client tools."
  exit 1
fi

if [[ ! -f "$SCRIPT" ]]; then
  echo "Missing bootstrap script: $SCRIPT"
  exit 1
fi

# Wait for PostgreSQL to accept connections.
for i in $(seq 1 60); do
  if pg_isready -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" >/dev/null 2>&1; then
    echo "PostgreSQL is reachable."
    break
  fi
  echo "Waiting for PostgreSQL (${i}/60)..."
  sleep 2
  if [[ "$i" == "60" ]]; then
    echo "PostgreSQL did not become ready in time."
    exit 1
  fi
done

# Create the database if it does not exist yet.
if ! psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d postgres \
      -tAc "SELECT 1 FROM pg_database WHERE datname = '$PG_DB'" | grep -q 1; then
  echo "Creating database '$PG_DB' ..."
  psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d postgres -c "CREATE DATABASE \"$PG_DB\""
fi

echo "Applying bootstrap.postgresql.sql on database '$PG_DB' ..."
psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d "$PG_DB" \
  -v ON_ERROR_STOP=1 -f "$SCRIPT"

echo "Bootstrap finished."
