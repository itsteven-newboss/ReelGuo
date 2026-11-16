#!/bin/sh
# Database backup: a pg_dump archive (custom format, compressed) in $BACKUP_DIR, kept $BACKUP_KEEP_DAYS days,
# and copied off the server when $BACKUP_REMOTE is set (any rclone remote, e.g. an R2 / S3 bucket).
#
# Runs in the `backup` service of infra/docker-compose.yml (postgres image, so pg_dump matches the server).
# Connection: the standard PGHOST / PGPORT / PGUSER / PGPASSWORD / PGDATABASE variables.
#
# Off-site copy, configured entirely through environment variables (no config file), for example Cloudflare R2:
#   BACKUP_REMOTE=r2:reelguo-backups/db
#   RCLONE_CONFIG_R2_TYPE=s3  RCLONE_CONFIG_R2_PROVIDER=Cloudflare  RCLONE_CONFIG_R2_ENDPOINT=https://<account>.r2.cloudflarestorage.com
#   RCLONE_CONFIG_R2_ACCESS_KEY_ID=...  RCLONE_CONFIG_R2_SECRET_ACCESS_KEY=...
# Use a bucket the app's own keys cannot write to, so a compromised app server can't delete its backups.
#
# Alerting: set BACKUP_PING_URL to a dead man's switch (e.g. a healthchecks.io check expecting a ping every 24 h);
# it is pinged only after a successful backup, so silence means backups stopped.
set -eu

DIR="${BACKUP_DIR:-/backups}"
KEEP_DAYS="${BACKUP_KEEP_DAYS:-14}"
DB="${PGDATABASE:-reelguo}"
mkdir -p "$DIR"

stamp=$(date -u +%Y%m%dT%H%M%SZ)
file="$DIR/$DB-$stamp.dump"
started=$(date +%s)

# Write to a temporary name first, so a failed or interrupted dump never looks like a good backup.
pg_dump --format=custom --compress=6 --no-owner --no-privileges --file="$file.partial" "$DB"
mv "$file.partial" "$file"

# The archive must at least be readable; the real proof is restore-test.sh.
pg_restore --list "$file" > /dev/null
(cd "$DIR" && sha256sum "$(basename "$file")" > "$(basename "$file").sha256")

size=$(du -h "$file" | cut -f1)
echo "$(date -u +%FT%TZ) backup ok: $file ($size, $(( $(date +%s) - started )) s)"

if [ -n "${BACKUP_REMOTE:-}" ]; then
  rclone copy "$file" "$BACKUP_REMOTE" --s3-no-check-bucket
  rclone copy "$file.sha256" "$BACKUP_REMOTE" --s3-no-check-bucket
  echo "$(date -u +%FT%TZ) copied to $BACKUP_REMOTE"
fi

# Local retention only: keep off-site retention as a lifecycle rule on the bucket (e.g. 35 days).
find "$DIR" -name "$DB-*.dump*" -type f -mtime +"$KEEP_DAYS" -print -delete

if [ -n "${BACKUP_PING_URL:-}" ]; then wget -q -T 10 -O /dev/null "$BACKUP_PING_URL" || echo "warning: could not ping $BACKUP_PING_URL"; fi
