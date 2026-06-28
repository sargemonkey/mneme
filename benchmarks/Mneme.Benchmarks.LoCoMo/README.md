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

## Run it — with GitHub Models (Copilot's model catalog)

The easiest "use Copilot models" path. GitHub Models exposes OpenAI, DeepSeek,
Llama, and other models over an OpenAI-compatible inference endpoint
(`https://models.github.ai/inference`), authenticated with a GitHub token that
carries the **`models:read`** scope.

```pwsh
$env:MNEME_LLM_PROVIDER = "github-models"
$env:GITHUB_TOKEN       = "ghp_..."   # fine-grained PAT or GitHub App token with models:read

# Optional overrides (these are the defaults):
$env:MNEME_LLM_MODEL   = "openai/gpt-4o-mini"
$env:MNEME_EMBED_MODEL = "openai/text-embedding-3-small"
$env:MNEME_EMBED_DIM   = "1536"

# Download the real dataset first (see below), then:
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.LoCoMo -- --dataset path/to/locomo10.json
```

Notes:
- Model ids are **publisher-prefixed** (`openai/gpt-4o-mini`,
  `openai/text-embedding-3-small`). Browse the catalog at
  [github.com/marketplace/models](https://github.com/marketplace/models).
- Create the token under Settings → Developer settings → fine-grained PAT, with
  the **Models** permission set to read. A classic PAT with `models:read` works too.
- GitHub Models free API usage is **rate limited** — for the full 1,540-question
  LoCoMo set you may hit limits; use `--limit` to evaluate a subset, or upgrade
  to paid usage. The harness processes sequentially and you can resume by
  re-running with a smaller slice.

## Run it — any other OpenAI-compatible endpoint (OpenAI / Azure / Ollama / vLLM)

Point the base URL + key at your provider:

```pwsh
$env:MNEME_LLM_BASE_URL = "https://api.openai.com"
$env:MNEME_LLM_API_KEY  = "sk-..."
$env:MNEME_LLM_MODEL    = "gpt-4o-mini"
$env:MNEME_EMBED_MODEL  = "text-embedding-3-small"
$env:MNEME_EMBED_DIM    = "1536"
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.LoCoMo -- --dataset path/to/locomo10.json
```

Optional: `MNEME_EMBED_BASE_URL` / `MNEME_EMBED_API_KEY` if embeddings live on
a different endpoint than chat. `--k <int>` sets retrieval depth (default 10);
`--limit <n>` caps the number of conversations.

## Resuming + outputs (rate-limit friendly)

Every graded question is appended to `results.jsonl` immediately, so a run that
hits a rate limit or is `Ctrl-C`'d can be **resumed**: re-run the same command
and it skips every question already in the file (no repeated LLM calls) and
replays their grades into the aggregate. Whole conversations that are fully
graded skip ingest + embedding entirely.

```pwsh
# Outputs default to <build-dir>/locomo-results/. Override with --out:
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.LoCoMo -- --dataset locomo10.json --out C:\runs\mneme-locomo

# ... interrupt any time (Ctrl-C). Then resume — same command, picks up where it stopped:
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.LoCoMo -- --dataset locomo10.json --out C:\runs\mneme-locomo

# Start over from scratch:
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.LoCoMo -- --dataset locomo10.json --out C:\runs\mneme-locomo --fresh
```

Two artifacts land in the output directory:
- **`results.jsonl`** — one JSON object per graded question (the resume log).
- **`results.csv`** — `sample_id, question_index, category_id, category, correct,
  context_tokens, question, gold, predicted` — open in a spreadsheet for error
  analysis (filter `correct=0` to see every miss with its retrieved-context size).
- **`results.md`** — a ready-to-paste Markdown report: run metadata (models,
  top-k, mean context tokens), per-category accuracy, and a reference row with
  the latest published Mem0 / Zep LoCoMo numbers for side-by-side context.

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
