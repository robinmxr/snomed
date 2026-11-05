-- Rebuilds concept_ancestor breadth first. Safe to re-run.
-- A single recursive CTE would enumerate every path, and multiple inheritance makes paths explode;
-- here each round only extends pairs found in the previous round, and the primary key drops pairs
-- already known. Because rounds go one level at a time, the first depth recorded is the shortest.
SET search_path TO :"schema", public;

DO $$
DECLARE
    added bigint;
    depth int := 1;
BEGIN
    CREATE TEMP TABLE isa ON COMMIT DROP AS
    SELECT r.source_id AS child, r.destination_id AS parent
    FROM relationship r
    JOIN concept c ON c.concept_id = r.source_id AND c.active
    JOIN concept p ON p.concept_id = r.destination_id AND p.active
    WHERE r.active AND r.type_id = 116680003
    GROUP BY 1, 2;
    CREATE INDEX ON isa (child);
    ANALYZE isa;

    TRUNCATE concept_ancestor;
    -- Indexes are dropped during the build and recreated once at the end, instead of being updated per insert.
    ALTER TABLE concept_ancestor DROP CONSTRAINT IF EXISTS concept_ancestor_pkey;
    DROP INDEX IF EXISTS ix_concept_ancestor_descendant;

    CREATE TEMP TABLE frontier ON COMMIT DROP AS
    SELECT parent AS ancestor_id, child AS descendant_id FROM isa;
    INSERT INTO concept_ancestor SELECT ancestor_id, descendant_id, 1 FROM frontier;
    CREATE TEMP TABLE seen ON COMMIT DROP AS SELECT ancestor_id, descendant_id FROM frontier;
    CREATE UNIQUE INDEX ON seen (descendant_id, ancestor_id);

    LOOP
        depth := depth + 1;
        CREATE TEMP TABLE next_frontier ON COMMIT DROP AS
        SELECT DISTINCT i.parent AS ancestor_id, f.descendant_id
        FROM frontier f
        JOIN isa i ON i.child = f.ancestor_id
        WHERE NOT EXISTS (SELECT 1 FROM seen s
                          WHERE s.descendant_id = f.descendant_id AND s.ancestor_id = i.parent);
        GET DIAGNOSTICS added = ROW_COUNT;
        EXIT WHEN added = 0;

        INSERT INTO concept_ancestor SELECT ancestor_id, descendant_id, depth FROM next_frontier;
        INSERT INTO seen SELECT ancestor_id, descendant_id FROM next_frontier;
        DROP TABLE frontier;
        ALTER TABLE next_frontier RENAME TO frontier;
        ANALYZE frontier;
        RAISE NOTICE 'closure depth %: % new pairs', depth, added;
    END LOOP;

    ALTER TABLE concept_ancestor ADD PRIMARY KEY (ancestor_id, descendant_id);
    CREATE INDEX ix_concept_ancestor_descendant ON concept_ancestor (descendant_id, ancestor_id);
END
$$;

ANALYZE concept_ancestor;
