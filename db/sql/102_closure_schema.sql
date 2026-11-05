-- Transitive closure of active IS-A relationships between active concepts. Idempotent.
-- Filled by 201_closure.sql at load time.
SET search_path TO :"schema", public;

CREATE TABLE IF NOT EXISTS concept_ancestor (
    ancestor_id   bigint NOT NULL,
    descendant_id bigint NOT NULL,
    min_depth     int    NOT NULL CHECK (min_depth > 0),  -- shortest IS-A path length
    PRIMARY KEY (ancestor_id, descendant_id)               -- "all descendants of X"
);
CREATE INDEX IF NOT EXISTS ix_concept_ancestor_descendant
    ON concept_ancestor (descendant_id, ancestor_id);       -- "all ancestors of X"
