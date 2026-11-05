-- Denormalized search table and its indexes. Idempotent.
SET search_path TO :"schema", public;

-- Which semantic tags are searchable. Edit this table, then re-run 004_populate.sql.
CREATE TABLE IF NOT EXISTS search_config (
    semantic_tag varchar(100) PRIMARY KEY
);
INSERT INTO search_config (semantic_tag) VALUES
    ('finding'), ('disorder'), ('situation'), ('procedure'), ('body structure'),
    ('substance'), ('organism'), ('observable entity'), ('physical object')
ON CONFLICT DO NOTHING;

-- One row per active, non-FSN description of an active concept in a configured tag.
CREATE TABLE IF NOT EXISTS search_term (
    description_id bigint PRIMARY KEY,
    concept_id     bigint NOT NULL,
    term           text   NOT NULL,
    term_lower     text   NOT NULL GENERATED ALWAYS AS (lower(term)) STORED,
    term_length    int    NOT NULL GENERATED ALWAYS AS (length(term)) STORED,
    semantic_tag   varchar(100) NOT NULL,
    preferred_term text   NOT NULL,
    children_count int    NOT NULL DEFAULT 0,
    tsv            tsvector GENERATED ALWAYS AS (to_tsvector('simple', term)) STORED
);

-- All-words and prefix matching.
CREATE INDEX IF NOT EXISTS ix_search_term_tsv      ON search_term USING gin (tsv);
-- Typo tolerance (word similarity) and substring matching.
CREATE INDEX IF NOT EXISTS ix_search_term_trgm     ON search_term USING gin (term gin_trgm_ops);
-- Exact and prefix ranking, suggest-as-you-type.
CREATE INDEX IF NOT EXISTS ix_search_term_lower    ON search_term (term_lower text_pattern_ops);
CREATE INDEX IF NOT EXISTS ix_search_term_concept  ON search_term (concept_id);
CREATE INDEX IF NOT EXISTS ix_search_term_tag      ON search_term (semantic_tag);
