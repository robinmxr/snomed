# Hierarchy query timing

Question: the hierarchy methods use a correlated subquery per row. Is that a problem?

## Method

- Harness: a throwaway console program (not committed) that calls `GetChildrenAsync` and `GetParentsAsync`
  on the repository, 1 warm-up call and 15 timed calls on one open context, median reported.
- Concept: one with 260 active children, `limit=50`, so 50 rows are built per call.
- Data: full licensed release loaded locally (1,693,873 descriptions, 3,544,687 relationships), indexes from `db/sql/002_tables.sql`.
- Hardware: Apple M1 Pro, 16 GB RAM, PostgreSQL 18.3 on the same machine, client on localhost.
- Date: 2026-11-05.

## Results

| Version | Children (50 of 260), median | Parents, median |
|---|---|---|
| Before (EF subqueries, unordered `FirstOrDefault` for the preferred term) | 3.5 ms | 1.6 ms |
| After (same shape, deterministic preferred term, existence check) | 9.4 ms | 2.0 ms |

## Reading

- With the partial indexes on `relationship` and `description`, the per-row subqueries are index lookups and the
  cost stays in single-digit milliseconds. They are not a bottleneck at these limits, so they were not rewritten.
- The "after" version is slower on purpose: the preferred term is now chosen by shortest synonym then
  alphabetical, the same rule as the search table. The earlier version took whichever row the database returned
  first, which is not deterministic. The ordered subquery and the extra existence check cost about 6 ms here.
- Single-machine, single-concept numbers with a handful of samples. Not a benchmark; see `docs/benchmarks.md`
  for the repeatable load test.
