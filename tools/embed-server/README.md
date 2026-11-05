# SapBERT embedding server

A small FastAPI server that serves [SapBERT](https://huggingface.co/cambridgeltl/SapBERT-from-PubMedBERT-fulltext)
over Ollama's `/api/embed` protocol, so the .NET `embed` and `eval` commands work with it unchanged.

Not tested in this repository's CI. It needs a Python environment and downloads the model (about 440 MB) on first start.

## Windows with an NVIDIA GPU

RTX 50-series cards need a PyTorch build for CUDA 12.8 or newer.

```powershell
cd tools\embed-server
py -3.12 -m venv .venv
.venv\Scripts\activate
pip install torch --index-url https://download.pytorch.org/whl/cu128
pip install -r requirements.txt
uvicorn server:app --host 0.0.0.0 --port 8000
```

Check it: open http://localhost:8000/health. `"device": "cuda"` means the GPU is in use.
