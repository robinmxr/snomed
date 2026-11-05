# Running the embedding bake-off

The code for semantic and hybrid search is complete and tested with a deterministic test-only embedding
client. Real vectors need a real model, and generating them is GPU work. This runbook covers doing that on a
separate Windows PC with an NVIDIA GPU.

## Choose a layout

**A. GPU PC serves embeddings only (simplest).** The database and the .NET commands stay on the machine that
already has the data loaded. The Windows PC only runs Ollama and the SapBERT server, reachable over the LAN.

**B. Everything on the GPU PC.** Install Docker Desktop, then follow the README to start Postgres and load your
SNOMED CT release there (`db/load.sh` runs in Git Bash or WSL). Latency numbers then come from one machine.

Layout A adds a network round trip to each query embedding, so state the layout in `--hardware` when you run `eval`.

## 1. General model: nomic-embed-text through Ollama

On the Windows PC:

```powershell
ollama pull nomic-embed-text
# Layout A only: let other machines reach Ollama, then restart it
setx OLLAMA_HOST 0.0.0.0
```

On the machine with the database (replace the URL for layout A):

```sh
dotnet run --project src/SnomedSearch.Console -- embed --profile nomic --url http://GPU-PC:11434
```

The command prints progress and an ETA. Ctrl+C is safe: re-run the same command and it continues where it
stopped. It finishes by building the HNSW index for this model.

## 2. Clinical model: SapBERT

Start the server on the Windows PC (`tools/embed-server/README.md`), then:

```sh
dotnet run --project src/SnomedSearch.Console -- embed --profile sapbert --url http://GPU-PC:8000
```

Both models are stored side by side under different keys (`nomic-embed-text`, `sapbert`), each with its own index.

## 3. Evaluate both

```sh
dotnet run --project src/SnomedSearch.Console -- eval --profile nomic   --url http://GPU-PC:11434 --hardware "RTX 5060 8 GB, layout A"
dotnet run --project src/SnomedSearch.Console -- eval --profile sapbert --url http://GPU-PC:8000  --hardware "RTX 5060 8 GB, layout A"
```

Each run writes its own section into `docs/evaluation.md` with recall@1/5/10, MRR and p50/p95 latency for
lexical, semantic and hybrid. Hybrid settings can be varied with `--rrf-k`, `--lexical-candidates`,
`--semantic-candidates` and `--ef-search`; use `--label` to keep variants apart.

## 4. Decide and record

Fill in `docs/embedding-choice.md` from the two sections: recall@10 first, p95 latency second. Then enable the
chosen model for the API in `.env`:

```sh
Embedding__BaseUrl=http://GPU-PC:11434
Embedding__Model=nomic-embed-text
Embedding__QueryPrefix=search_query: 
```

Without these settings the API answers `mode=semantic` and `mode=hybrid` with 501.

## Useful options for `embed`

| Option | Meaning |
|---|---|
| `--tags finding,disorder` | Semantic tags to embed (default finding, disorder) |
| `--batch 64` | Descriptions per request |
| `--max N` | Stop after N descriptions (for a quick trial) |
| `--key NAME` | Store under a different key, to keep two runs of one model |
| `--reembed` | Delete this key's vectors first (needed when the dimension changes) |
