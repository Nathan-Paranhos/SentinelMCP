#!/bin/sh
# Executado pelo entrypoint oficial do PostgreSQL apenas na primeira inicialização do volume.
# Cria o papel de menor privilégio (sentinel_app) e o login usado pela API. Os GRANTs nas
# tabelas são aplicados pela migração 003 (serviço sentinel-migrate do compose).
set -eu

if [ -z "${SENTINEL_APP_DB_PASSWORD:-}" ]; then
    echo "SENTINEL_APP_DB_PASSWORD não definido" >&2
    exit 1
fi

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -v app_password="$SENTINEL_APP_DB_PASSWORD" <<'EOSQL'
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sentinel_app') THEN
        CREATE ROLE sentinel_app NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;
CREATE ROLE sentinel_app_login LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD :'app_password' IN ROLE sentinel_app;
EOSQL
