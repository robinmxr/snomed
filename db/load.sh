#!/usr/bin/env bash
# Loads a SNOMED CT release (RF2 snapshot files) that YOU downloaded, then rebuilds derived tables.
# Usage: db/load.sh /path/to/SnomedCT_Release_folder
# Re-running reloads everything from scratch, so it is idempotent.
set -euo pipefail
ARG="${1:?usage: load.sh /path/to/release-folder}"
[ -d "$ARG" ] || { echo "not a directory: $ARG" >&2; exit 1; }
DIR="$(cd "$ARG" && pwd)"   # resolve before changing directory
cd "$(dirname "$0")"
. ./lib.sh

find_file() { find "$DIR" -type f -name "$1" -path '*Snapshot*' | sort | head -n 1; }

CONCEPT=$(find_file 'sct2_Concept_Snapshot*.txt')
DESCRIPTION=$(find_file 'sct2_Description_Snapshot-en*.txt')
RELATIONSHIP=$(find_file 'sct2_Relationship_Snapshot*.txt')
TEXTDEF=$(find_file 'sct2_TextDefinition_Snapshot-en*.txt')
for pair in "concept:$CONCEPT" "description:$DESCRIPTION" "relationship:$RELATIONSHIP"; do
  [ -n "${pair#*:}" ] || { echo "missing ${pair%%:*} snapshot file under $DIR" >&2; exit 1; }
done

# RF2 is tab separated with a header row and no quoting, so use a quote character that never occurs.
copy_in() { # table  file
  run_sql -c "COPY $1 FROM STDIN WITH (FORMAT csv, DELIMITER E'\t', QUOTE E'\b', HEADER true)" < "$2"
}

echo "load: staging tables"
run_sql < load/01_staging.sql

# Stage with the schema on the search path by prefixing table names.
echo "load: $(basename "$CONCEPT")";      copy_in "$SCHEMA.stg_concept" "$CONCEPT"
echo "load: $(basename "$DESCRIPTION")";  copy_in "$SCHEMA.stg_description" "$DESCRIPTION"
echo "load: $(basename "$RELATIONSHIP")"; copy_in "$SCHEMA.stg_relationship" "$RELATIONSHIP"
if [ -n "$TEXTDEF" ]; then echo "load: $(basename "$TEXTDEF")"; copy_in "$SCHEMA.stg_text_definition" "$TEXTDEF"
else echo "load: no text definition file found, skipping"; fi

echo "load: casting into typed tables"
run_sql < load/02_cast.sql

echo "load: building semantic tags and search table"
run_sql < sql/004_populate.sql
for f in sql/2*.sql; do   # post-load derivations (hierarchy closure) use the 2xx range
  [ -e "$f" ] || continue
  echo "load: $f"; run_sql < "$f"
done
echo "load: done"
