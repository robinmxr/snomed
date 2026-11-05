"""SapBERT embedding server speaking Ollama's POST /api/embed protocol.

The .NET client talks to Ollama and to this server the same way, so both models in the
bake-off go through identical code. Run:  uvicorn server:app --host 0.0.0.0 --port 8000
"""
import os

import torch
from fastapi import FastAPI
from pydantic import BaseModel
from transformers import AutoModel, AutoTokenizer

MODEL_NAME = os.environ.get("EMBED_MODEL", "cambridgeltl/SapBERT-from-PubMedBERT-fulltext")
BATCH = int(os.environ.get("EMBED_BATCH", "256"))
MAX_TOKENS = int(os.environ.get("EMBED_MAX_TOKENS", "32"))  # SapBERT was trained on short names

device = "cuda" if torch.cuda.is_available() else "cpu"
tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME)
model = AutoModel.from_pretrained(MODEL_NAME).to(device).eval()
if device == "cuda":
    model = model.half()

app = FastAPI()


class EmbedRequest(BaseModel):
    model: str | None = None
    input: list[str] | str


@app.get("/health")
def health():
    return {"model": MODEL_NAME, "device": device, "cuda": torch.version.cuda}


@app.post("/api/embed")
def embed(req: EmbedRequest):
    texts = [req.input] if isinstance(req.input, str) else req.input
    vectors: list[list[float]] = []
    with torch.inference_mode():
        for start in range(0, len(texts), BATCH):
            batch = tokenizer(texts[start:start + BATCH], padding=True, truncation=True,
                              max_length=MAX_TOKENS, return_tensors="pt").to(device)
            # SapBERT represents a name by the [CLS] token of the last layer.
            cls = model(**batch).last_hidden_state[:, 0, :]
            vectors.extend(cls.float().cpu().tolist())
    return {"model": req.model or "sapbert", "embeddings": vectors}
