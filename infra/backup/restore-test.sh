#!/bin/sh
# Restore test: proves a backup can actually be restored, and times it. Restores the newest dump (or the file
# given) into a throwaway database next to the live one, checks it, compares row counts, then drops it.
# The live database is only read. Run it monthly and after any change to backups; log the result in the runbook.
#
#   docker compose -f infra/docker-compose.yml --profile app run --rm backup "sh /scripts/restore-test.sh [dump-file]"
#
# Exit code 0 = restore worked and the data looks right.
set -eu

DIR="${BACKUP_DIR:-/backups}"
DB="${PGDATABASE:-reelguo}"
SCRATCH="${DB}_restore_test"
file="${1:-$(ls -1t "$DIR"/"$DB"-*.dump 2>/dev/null | head -n 1)}"
[ -n "$file" ] && [ -f "$file" ] || { echo "No backup found in $DIR" >&2; exit 1; }

if [ -f "$file.sha256" ]; then
  (cd "$(dirname "$file")" && sha256sum -c "$(basename "$file").sha256" > /dev/null) || { echo "Checksum mismatch: $file" >&2; exit 1; }
fi

cleanup() { psql -q -d postgres -c "DROP DATABASE IF EXISTS \"$SCRATCH\" WITH (FORCE)" > /dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup
psql -q -d postgres -c "CREATE DATABASE \"$SCRATCH\" ENCODING 'UTF8' TEMPLATE template0" > /dev/null

started=$(date +%s)
pg_restore --no-owner --no-privileges --exit-on-error --jobs=2 --dbname="$SCRATCH" "$file"
seconds=$(( $(date +%s) - started ))

count() { psql -tA -d "$1" -c "SELECT count(*) FROM $2"; }
fail=0
echo "Restore test · $(date -u +%FT%TZ) · $(basename "$file") · restored in ${seconds} s"
echo "  latest migration: $(psql -tA -d "$SCRATCH" -c 'SELECT max("MigrationId") FROM "__EFMigrationsHistory"')"
for table in users series episodes posts comments reports blocked_countries audit_log; do
  restored=$(count "$SCRATCH" "$table")
  live=$(count "$DB" "$table" 2>/dev/null || echo "?")
  # The live database has moved on since the backup, so it may have more rows, never far fewer.
  status="ok"
  if [ "$live" != "?" ] && [ "$restored" -gt "$live" ]; then status="MORE THAN LIVE?"; fi
  if [ "$live" != "?" ] && [ "$live" -gt 0 ] && [ "$restored" -eq 0 ]; then status="EMPTY"; fail=1; fi
  printf "  %-18s restored %8s   live %8s   %s\n" "$table" "$restored" "$live" "$status"
done
channels=$(count "$SCRATCH" categories)
[ "$channels" -ge 4 ] || { echo "  categories: expected the 4 seeded channels, found $channels"; fail=1; }

if [ "$fail" -eq 0 ]; then echo "PASS"; else echo "FAIL"; fi
exit "$fail"
