# Local reranker model

The `--reranker onnx` option uses a local cross-encoder for a *true*
two-stage retrieve-then-rerank (see `OnnxCrossEncoderReranker`). The model
binary is **not vendored** (22 MB, gitignored). Download it once:

```pwsh
$base = "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/main"
Invoke-WebRequest "$base/onnx/model_quantized.onnx" -OutFile ms-marco-MiniLM-L6.onnx
Invoke-WebRequest "$base/vocab.txt"                 -OutFile vocab.txt
```

`vocab.txt` is committed (226 KB); only the `.onnx` weights are ignored.

This is the "host brings a local model" routing of Mneme's `IReranker`
seam — fully offline, no API key. Swap in any ONNX BERT cross-encoder
(bge-reranker, etc.) by dropping its `model.onnx` + `vocab.txt` here.
