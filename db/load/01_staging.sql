-- Text-typed staging tables that COPY fills straight from the RF2 files.
SET search_path TO :"schema", public;
DROP TABLE IF EXISTS stg_concept, stg_description, stg_relationship, stg_text_definition;
CREATE UNLOGGED TABLE stg_concept (id text, effective text, active text, module text, def_status text);
CREATE UNLOGGED TABLE stg_description (id text, effective text, active text, module text, concept text,
                                       lang text, type text, term text, case_sig text);
CREATE UNLOGGED TABLE stg_relationship (id text, effective text, active text, module text, source text,
                                        destination text, grp text, type text, char_type text, modifier text);
CREATE UNLOGGED TABLE stg_text_definition (LIKE stg_description);
