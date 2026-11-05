-- Rebuilds the derived tables from the raw release tables. Safe to re-run.
SET search_path TO :"schema", public;

BEGIN;

-- 1. Semantic tag: the last parenthesised suffix of the active fully specified name.
TRUNCATE semantic_tag;
INSERT INTO semantic_tag (concept_id, fully_specified_name, semantic_tag)
SELECT DISTINCT ON (d.concept_id)
       d.concept_id,
       d.term,
       substring(d.term from '\(([^()]+)\)\s*$')
FROM description d
JOIN concept c ON c.concept_id = d.concept_id AND c.active
WHERE d.active
  AND d.type_id = 900000000000003001            -- fully specified name
  AND d.term ~ '\([^()]+\)\s*$'
ORDER BY d.concept_id, d.effective_time DESC, d.description_id;

-- 2. Preferred term: shortest active synonym (a heuristic, no language reference set is loaded).
CREATE TEMP TABLE tmp_preferred ON COMMIT DROP AS
SELECT DISTINCT ON (d.concept_id) d.concept_id, d.term
FROM description d
WHERE d.active AND d.type_id = 900000000000013009
ORDER BY d.concept_id, length(d.term), d.term;

-- 3. Child counts from active IS-A relationships.
CREATE TEMP TABLE tmp_children ON COMMIT DROP AS
SELECT destination_id AS concept_id, count(*)::int AS n
FROM relationship
WHERE active AND type_id = 116680003
GROUP BY destination_id;

-- 4. Search table.
TRUNCATE search_term;
INSERT INTO search_term (description_id, concept_id, term, semantic_tag, preferred_term, children_count)
SELECT d.description_id,
       d.concept_id,
       d.term,
       st.semantic_tag,
       COALESCE(p.term, d.term),
       COALESCE(ch.n, 0)
FROM description d
JOIN concept c        ON c.concept_id = d.concept_id AND c.active
JOIN semantic_tag st  ON st.concept_id = d.concept_id
JOIN search_config sc ON sc.semantic_tag = st.semantic_tag
LEFT JOIN tmp_preferred p ON p.concept_id = d.concept_id
LEFT JOIN tmp_children ch ON ch.concept_id = d.concept_id
WHERE d.active
  AND d.type_id <> 900000000000003001;          -- exclude fully specified names

COMMIT;

ANALYZE semantic_tag;
ANALYZE search_term;
