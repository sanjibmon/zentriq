#!/usr/bin/env bash
# Applies all migrations to a throw-away database exactly as the C# migrator does
# (bootstrap -> versioned files in order -> apply_app_grants), then runs assertions as the
# least-privilege application role.
#
# Usage: PGHOST=127.0.0.1 PGPORT=5432 PGUSER=postgres PGPASSWORD=... ./run-db-tests.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
DB="zentriq_dbtest_$$"
OWNER="zt_owner_$$"; APP="zt_app_$$"
OWNER_PW="owner-$RANDOM-$RANDOM"; APP_PW="app-$RANDOM-$RANDOM"

su_psql() { psql -v ON_ERROR_STOP=1 -X -q -d postgres "$@"; }
cleanup() {
  su_psql -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)" >/dev/null 2>&1 || true
  su_psql -c "DROP ROLE IF EXISTS $APP" -c "DROP ROLE IF EXISTS $OWNER" >/dev/null 2>&1 || true
}
trap cleanup EXIT

su_psql -c "CREATE ROLE $OWNER LOGIN PASSWORD '$OWNER_PW' NOSUPERUSER NOCREATEDB NOCREATEROLE"
su_psql -c "CREATE ROLE $APP LOGIN PASSWORD '$APP_PW' NOSUPERUSER NOCREATEDB NOCREATEROLE"
su_psql -c "CREATE DATABASE $DB OWNER $OWNER ENCODING 'UTF8' TEMPLATE template0"
psql -v ON_ERROR_STOP=1 -X -q -d "$DB" -c "CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public" -c "REVOKE CREATE ON SCHEMA public FROM PUBLIC" -c "REVOKE ALL ON DATABASE $DB FROM PUBLIC" -c "GRANT CONNECT ON DATABASE $DB TO $APP"

as_owner() { PGUSER="$OWNER" PGPASSWORD="$OWNER_PW" psql -v ON_ERROR_STOP=1 -X -q -d "$DB" "$@"; }
as_app()   { PGUSER="$APP"   PGPASSWORD="$APP_PW"   psql -v ON_ERROR_STOP=1 -X -q -d "$DB" "$@"; }

as_owner -f "$ROOT/database/bootstrap.sql"
for f in $(ls "$ROOT"/database/migrations/V*.sql | sort); do
  name="$(basename "$f")"; ver="${name:1:4}"
  echo "applying $name"
  as_owner -1 -f "$f"
  sum="$(sha256sum "$f" | cut -d' ' -f1)"
  as_owner -c "INSERT INTO zentriq.schema_migrations(version,name,checksum,duration_ms,product_version) VALUES ($((10#$ver)),'$name','$sum',0,'test')"
done
as_owner -c "INSERT INTO zentriq.install_state (product_version) VALUES ('test') ON CONFLICT DO NOTHING"
as_owner -c "SELECT zentriq.apply_app_grants('$APP')"

# Re-running grants must be idempotent.
as_owner -c "SELECT zentriq.apply_app_grants('$APP')"

as_app -f "$ROOT/tests/integration/db/assertions.sql"
echo "DB TESTS: PASS"
