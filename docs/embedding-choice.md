# Embedding model choice

**Status: not decided yet.** No embedding model has been run against the real data, so this file
holds no numbers. It records the method, so the decision can be made from measurements.

## Candidates

| Model | Type | Dimension | Served by | Why it is a candidate |
|---|---|---|---|---|
| `nomic-embed-text` | General-purpose text embedding | 768 | Ollama | Runs locally, already part of the owner's tooling |
| `cambridgeltl/SapBERT-from-PubMedBERT-fulltext` | Biomedical entity names, trained on UMLS synonyms | 768 | `tools/embed-server` | Built for the exact task of matching names to concepts |

## Method

1. Embed the active descriptions of finding and disorder concepts with each model (`docs/semantic-search-runbook.md`).
2. Run `eval` for each model. The pairs in `eval/pairs.csv` are everyday phrases, written before any model was
   tried, so they were not tuned to either one.
3. Pick by **recall@10 in semantic mode** first. If the two are within 0.05 of each other, pick the lower
   **p95 query latency**. Also note which model makes **hybrid** mode best, since that is the default for users.

## Result

To be filled in from `docs/evaluation.md` after the runs: the recall and latency rows for each model, the
hardware, the date, and the choice with one sentence on why.

The lexical-only baseline is already measured there (recall@10 0.43 on 54 phrases), so any semantic model
has a concrete number to beat.
