#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
umask 077
exec 9>.release.lock
flock -n 9 || { echo 'Another release is running in this directory.' >&2; exit 1; }
compose() { docker compose --env-file .env.production -f deploy/docker-compose.production.yml "$@"; }
compose config --quiet
test -r deploy/production/tls/backend/backend.key
test -r deploy/production/tls/backend/backend.crt
test -r deploy/production/tls/backend/ca.crt
compose build api
compose up -d --wait postgres
# Stop-first: only one Production API can own this database.
compose stop api
mkdir -p backups
backup="backups/pre-release-$(date -u +%Y%m%dT%H%M%SZ).dump"
compose exec -T postgres pg_dump -U chat -d chat -Fc > "$backup.partial"
compose exec -T postgres pg_restore --list < "$backup.partial" > /dev/null
mv "$backup.partial" "$backup"
compose run --rm --no-deps migrate
compose up -d --wait --wait-timeout 180 api
compose ps
