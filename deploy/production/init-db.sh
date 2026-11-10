#!/bin/sh
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    --set=app_password="$CHAT_DB_PASSWORD" <<'SQL'
CREATE ROLE chat LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD :'app_password';
ALTER DATABASE chat OWNER TO chat;
GRANT ALL ON SCHEMA public TO chat;
SQL
