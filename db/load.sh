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
run_sql <<'SQL'
SET search_path TO :"schema", public;
DROP TABLE IF EXISTS stg_concept, stg_description, stg_relationship, stg_text_definition;
CREATE UNLOGGED TABLE stg_concept (id text, effective text, active text, module text, def_status text);
CREATE UNLOGGED TABLE stg_description (id text, effective text, active text, module text, concept text,
                                       lang text, type text, term text, case_sig text);
CREATE UNLOGGED TABLE stg_relationship (id text, effective text, active text, module text, source text,
                                        destination text, grp text, type text, char_type text, modifier text);
CREATE UNLOGGED TABLE stg_text_definition (LIKE stg_description);
SQL

# Stage with the schema on the search path by prefixing table names.
echo "load: $(basename "$CONCEPT")";      copy_in "$SCHEMA.stg_concept" "$CONCEPT"
echo "load: $(basename "$DESCRIPTION")";  copy_in "$SCHEMA.stg_description" "$DESCRIPTION"
echo "load: $(basename "$RELATIONSHIP")"; copy_in "$SCHEMA.stg_relationship" "$RELATIONSHIP"
if [ -n "$TEXTDEF" ]; then echo "load: $(basename "$TEXTDEF")"; copy_in "$SCHEMA.stg_text_definition" "$TEXTDEF"
else echo "load: no text definition file found, skipping"; fi

echo "load: casting into typed tables"
run_sql <<'SQL'
SET search_path TO :"schema", public;
BEGIN;
TRUNCATE concept, description, relationship, text_definition;
INSERT INTO concept
  SELECT id::bigint, to_date(effective,'YYYYMMDD'), active = '1', module::bigint, def_status::bigint FROM stg_concept;
INSERT INTO description
  SELECT id::bigint, to_date(effective,'YYYYMMDD'), active = '1', module::bigint, concept::bigint,
         lang, type::bigint, term, case_sig::bigint FROM stg_description;
INSERT INTO relationship
  SELECT id::bigint, to_date(effective,'YYYYMMDD'), active = '1', module::bigint, source::bigint,
         destination::bigint, grp::int, type::bigint, char_type::bigint, modifier::bigint FROM stg_relationship;
INSERT INTO text_definition
  SELECT id::bigint, to_date(effective,'YYYYMMDD'), active = '1', module::bigint, concept::bigint,
         lang, type::bigint, term, case_sig::bigint FROM stg_text_definition;
COMMIT;
DROP TABLE stg_concept, stg_description, stg_relationship, stg_text_definition;
ANALYZE concept; ANALYZE description; ANALYZE relationship; ANALYZE text_definition;
SQL

echo "load: building semantic tags and search table"
run_sql < sql/004_populate.sql
for f in sql/2*.sql; do   # post-load derivations (hierarchy closure) use the 2xx range
  [ -e "$f" ] || continue
  echo "load: $f"; run_sql < "$f"
done
echo "load: done"
