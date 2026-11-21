#!/bin/bash
# Back up the Postgres database to BACKUP_DIR (default ./backups).
# Usage: scripts/backup.sh [label]
set -euo pipefail

LABEL="${1:-$(date -u +%Y%m%dT%H%M%SZ)}"
BACKUP_DIR="${BACKUP_DIR:-./backups}"
CONTAINER="${POSTGRES_CONTAINER:-corebanking-postgres}"
DB_USER="${POSTGRES_USER:-corebanking}"
DB_NAME="${POSTGRES_DB:-corebanking}"
KEEP_DAYS="${BACKUP_KEEP_DAYS:-30}"

mkdir -p "$BACKUP_DIR"
OUT="$BACKUP_DIR/corebanking-$LABEL.sql.gz"

docker exec "$CONTAINER" pg_dump -U "$DB_USER" -d "$DB_NAME" -Fc --no-acl --no-owner \
  | gzip > "$OUT.tmp"
mv "$OUT.tmp" "$OUT"
chmod 600 "$OUT"
echo "Backup written: $OUT ($(du -h "$OUT" | cut -f1))"

find "$BACKUP_DIR" -name 'corebanking-*.sql.gz' -type f -mtime "+$KEEP_DAYS" -print -delete
echo "Retention: removed backups older than $KEEP_DAYS days"
