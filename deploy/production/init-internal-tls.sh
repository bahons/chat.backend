#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
umask 077
tls=deploy/production/tls
if [ -e "$tls/backend/backend.key" ] || [ -e "$tls/authority/ca.key" ]; then
    echo 'Internal TLS already exists; refusing to overwrite keys.' >&2
    exit 1
fi
mkdir -p "$tls/authority" "$tls/backend"
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj '/CN=chat internal CA' \
    -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' \
    -keyout "$tls/authority/ca.key" -out "$tls/authority/ca.crt"
openssl req -new -newkey rsa:3072 -nodes -subj '/CN=chat-backend.internal' \
    -keyout "$tls/backend/backend.key" -out "$tls/backend/backend.csr"
cat > "$tls/backend/backend.ext" <<'EXT'
subjectAltName=DNS:chat-backend.internal
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
EXT
openssl x509 -req -days 365 -in "$tls/backend/backend.csr" \
    -CA "$tls/authority/ca.crt" -CAkey "$tls/authority/ca.key" -CAcreateserial \
    -extfile "$tls/backend/backend.ext" -out "$tls/backend/backend.crt"
cp "$tls/authority/ca.crt" "$tls/backend/ca.crt"
chmod 755 "$tls/backend"
chmod 644 "$tls/backend/backend.crt" "$tls/backend/ca.crt"
chown 1654:1654 "$tls/backend/backend.key"
chmod 600 "$tls/backend/backend.key"
