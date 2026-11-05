#!/usr/bin/env bash
# Creates the schema, extensions, tables and indexes. Idempotent.
set -euo pipefail
cd "$(dirname "$0")"
. ./lib.sh
for f in sql/001_extensions.sql sql/002_tables.sql sql/003_search_schema.sql; do
  echo "migrate: $f"
  run_sql < "$f"
done
for f in sql/1*.sql; do   # later migrations (closure table, embeddings) use the 1xx range
  [ -e "$f" ] || continue
  echo "migrate: $f"; run_sql < "$f"
done
