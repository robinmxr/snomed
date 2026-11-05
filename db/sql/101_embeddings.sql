-- Embeddings for semantic search. Idempotent.
-- One row per (model, description). Several models can live side by side, each with its own
-- partial HNSW index, which the backfill command creates once it knows the dimension.
SET search_path TO :"schema", public;

CREATE TABLE IF NOT EXISTS embedding_model (
    model_key   text PRIMARY KEY,
    dimension   int  NOT NULL CHECK (dimension > 0),
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS description_embedding (
    model_key      text   NOT NULL REFERENCES embedding_model (model_key) ON DELETE CASCADE,
    description_id bigint NOT NULL,
    concept_id     bigint NOT NULL,
    semantic_tag   varchar(100) NOT NULL,
    text_hash      bytea  NOT NULL,          -- md5 of the exact text embedded, so edits trigger a re-embed
    dimension      int    NOT NULL,
    embedding      vector NOT NULL,
    embedded_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (model_key, description_id)
);
