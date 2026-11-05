# Shared helpers. Source this file; do not run it.
# PSQL may be set to any command that runs psql against the target database, for example
#   PSQL="docker compose exec -T db psql -U snomed -d snomed"
# Otherwise host psql is used with the SNOMED_DB_* variables (from the environment or .env).

if [ -z "${SNOMED_DB_HOST:-}" ] && [ -f "$(dirname "${BASH_SOURCE[0]}")/../.env" ]; then
  set -a; . "$(dirname "${BASH_SOURCE[0]}")/../.env"; set +a
fi
SCHEMA="${SNOMED_DB_SCHEMA:-snomed}"

# SNOMED_USE_DOCKER=1 (set by the Makefile) runs psql inside the compose service, so no local psql is needed.
if [ -z "${PSQL:-}" ] && [ "${SNOMED_USE_DOCKER:-0}" = "1" ]; then
  : "${SNOMED_DB_NAME:?SNOMED_DB_NAME is not set}" "${SNOMED_DB_USER:?SNOMED_DB_USER is not set}"
  PSQL="docker compose exec -T db psql -U $SNOMED_DB_USER -d $SNOMED_DB_NAME"
fi

if [ -z "${PSQL:-}" ]; then
  : "${SNOMED_DB_HOST:?SNOMED_DB_HOST is not set}" "${SNOMED_DB_PORT:?SNOMED_DB_PORT is not set}"
  : "${SNOMED_DB_NAME:?SNOMED_DB_NAME is not set}" "${SNOMED_DB_USER:?SNOMED_DB_USER is not set}"
  : "${SNOMED_DB_PASSWORD:?SNOMED_DB_PASSWORD is not set}"
  export PGPASSWORD="$SNOMED_DB_PASSWORD"
  PSQL="${PSQL_BIN:-psql} -h $SNOMED_DB_HOST -p $SNOMED_DB_PORT -U $SNOMED_DB_USER -d $SNOMED_DB_NAME"
fi

run_sql() { $PSQL -X -q -v ON_ERROR_STOP=1 -v schema="$SCHEMA" "$@"; }
