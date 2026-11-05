-- Casts staged text into the typed tables, replacing their contents. Safe to re-run.
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
