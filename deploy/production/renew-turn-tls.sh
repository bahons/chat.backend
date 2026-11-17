#!/bin/sh
set -eu
# Certbot deploy hook for the existing chat.dodapp.kz lineage only.
[ "${RENEWED_LINEAGE:-}" = /etc/letsencrypt/live/chat.dodapp.kz ] || exit 0
cd /home/ubuntu/chat
install -d -m 700 -o 65534 -g 65534 deploy/production/tls/turn
install -m 400 -o 65534 -g 65534 "$RENEWED_LINEAGE/fullchain.pem" deploy/production/tls/turn/fullchain.pem
install -m 400 -o 65534 -g 65534 "$RENEWED_LINEAGE/privkey.pem" deploy/production/tls/turn/privkey.pem
docker compose --env-file .env.production -f deploy/docker-compose.production.yml \
  -f deploy/docker-compose.webrtc.yml restart coturn
