-- Idempotent. Run with: psql -v ON_ERROR_STOP=1 -v schema=snomed -f 002_tables.sql
CREATE SCHEMA IF NOT EXISTS :"schema";
SET search_path TO :"schema", public;

-- Raw release tables (one row per RF2 snapshot row).
CREATE TABLE IF NOT EXISTS concept (
    concept_id           bigint PRIMARY KEY,
    effective_time       date,
    active               boolean NOT NULL,
    module_id            bigint,
    definition_status_id bigint
);

CREATE TABLE IF NOT EXISTS description (
    description_id       bigint PRIMARY KEY,
    effective_time       date,
    active               boolean NOT NULL,
    module_id            bigint,
    concept_id           bigint NOT NULL,
    language_code        varchar(8),
    type_id              bigint NOT NULL,
    term                 text NOT NULL,
    case_significance_id bigint
);

CREATE TABLE IF NOT EXISTS relationship (
    relationship_id        bigint PRIMARY KEY,
    effective_time         date,
    active                 boolean NOT NULL,
    module_id              bigint,
    source_id              bigint NOT NULL,
    destination_id         bigint NOT NULL,
    relationship_group     integer,
    type_id                bigint NOT NULL,
    characteristic_type_id bigint,
    modifier_id            bigint
);

CREATE TABLE IF NOT EXISTS text_definition (
    definition_id        bigint PRIMARY KEY,
    effective_time       date,
    active               boolean NOT NULL,
    module_id            bigint,
    concept_id           bigint NOT NULL,
    language_code        varchar(8),
    type_id              bigint,
    term                 text NOT NULL,
    case_significance_id bigint
);

-- Derived from the fully specified name suffix, rebuilt by 004_populate.sql.
CREATE TABLE IF NOT EXISTS semantic_tag (
    concept_id           bigint PRIMARY KEY,
    fully_specified_name text NOT NULL,
    semantic_tag         varchar(100) NOT NULL
);

-- Lookup indexes for hierarchy queries and descriptions.
CREATE INDEX IF NOT EXISTS ix_description_concept   ON description (concept_id) WHERE active;
CREATE INDEX IF NOT EXISTS ix_relationship_parent   ON relationship (destination_id) WHERE active AND type_id = 116680003;
CREATE INDEX IF NOT EXISTS ix_relationship_child    ON relationship (source_id)      WHERE active AND type_id = 116680003;
CREATE INDEX IF NOT EXISTS ix_text_definition_concept ON text_definition (concept_id) WHERE active;
CREATE INDEX IF NOT EXISTS ix_semantic_tag_tag      ON semantic_tag (semantic_tag);
