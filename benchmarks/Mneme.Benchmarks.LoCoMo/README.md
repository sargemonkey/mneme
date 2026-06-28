# Mneme.Benchmarks.LoCoMo

The **LoCoMo evaluation harness** — the apples-to-apples accuracy benchmark
that Mem0, Zep, and others report. It measures *answer quality* over very
long, multi-session conversations, unlike:

- [`Mneme.Benchmarks`](../Mneme.Benchmarks/) — a small internal recall probe.
- [`Mneme.Benchmarks.Perf`](../Mneme.Benchmarks.Perf/) — storage-layer latency.

## What it does

For each LoCoMo conversation:

1. **Ingest** every session turn into a dedicated Mneme workstream as an
   Evidence event stamped with the session's timestamp (bi-temporal).
2. **Embed** the workstream (`VectorIndex.BackfillAsync`) via the configured
   embedding model.
3. For each question, **retrieve** the top-k memory snippets with Mneme's
   hybrid semantic + lexical query, **answer** with the chat model using only
   those snippets, and **judge** the answer against the gold answer.
4. **Score** overall and per LoCoMo category (single-hop, multi-hop, temporal,
   open-domain, adversarial), plus the mean context tokens per query.

## Run it — real (turnkey, OpenAI-compatible)

Works with OpenAI, Azure OpenAI, Ollama, vLLM, or LM Studio — anything that
speaks the `/v1/chat/completions` + `/v1/embeddings` REST shape. Point the
base URL + key at your provider:

```pwsh
$env:MNEME_LLM_BASE_URL = "https://api.openai.com"
$env:MNEME_LLM_API_KEY  = "sk-..."
$env:MNEME_LLM_MODEL    = "gpt-4o-mini"
$env:MNEME_EMBED_MODEL  = "text-embedding-3-small"
$env:MNEME_EMBED_DIM    = "1536"

# Download the real dataset first (see below), then:
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.LoCoMo -- --dataset path/to/locomo10.json
```

Optional: `MNEME_EMBED_BASE_URL` / `MNEME_EMBED_API_KEY` if embeddings live on
a different endpoint than chat. `--k <int>` sets retrieval depth (default 10);
`--limit <n>` caps the number of conversations.

## Run it — dry-run (offline, no keys)

With no `MNEME_LLM_*` env set, the harness runs fully offline against a bundled
mini fixture using a bag-of-words embedder, an echo answerer, and a token-F1
judge:

```pwsh
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.LoCoMo
```

This exercises the entire pipeline (ingest → embed → retrieve → answer → judge
→ score) with no network. **The dry-run numbers are NOT a real LoCoMo score** —
the offline answerer just echoes the top snippet. It exists to prove the
plumbing works and to smoke-test changes.

## Getting the real dataset

LoCoMo is published by Snap Research:
<https://github.com/snap-research/locomo>. Download `locomo10.json` (10
conversations, ~1,540 questions) and pass it via `--dataset`. The loader is
tolerant of the official schema (multi-session `conversation` + `qa` arrays,
`adversarial_answer` for category 5).

## Interpreting results vs. Mem0 / Zep

- **Comparable axis:** overall + per-category accuracy, and mean context tokens
  per query (token efficiency). These line up directly with Mem0's LoCoMo 92.5
  / Zep's LongMemEval numbers.
- **Fair-comparison knobs:** retrieval depth (`--k`), the chat model, and the
  embedding model are all yours to set — use the same model the framework you're
  comparing against used, or hold the model fixed and vary only the memory layer.
- **What Mneme controls:** only the *retrieval* (hybrid semantic + lexical +
  recency, bi-temporal). The answer/judge model is external and identical across
  systems, so differences reflect memory quality, not the LLM.

> Numbers are machine- and model-specific. Record the model ids (printed in the
> report header) with any result you publish.
