# LoCoMo benchmark — analysis

This document records Mneme's LoCoMo results and an honest analysis of where
the gaps are versus published numbers from other memory layers (Mem0, Zep).

> **Reproducibility:** all runs use GitHub Models — `openai/gpt-4o-mini` for
> both answering and judging, `openai/text-embedding-3-small@1536` for
> retrieval — driven by `benchmarks/Mneme.Benchmarks.LoCoMo`. Numbers are
> machine-, model-, and sample-specific.

## Runs

### Balanced 50-question subset (10 per category, conv-26), k=20

| Ingest mode | Overall | single-hop | multi-hop | temporal | open-domain | adversarial | Mean ctx tokens |
|---|---:|---:|---:|---:|---:|---:|---:|
| `turns` (raw) | 40% | 70% | 10% | 60% | 40% | 20% | 646 |
| `facts` (distilled) | 42% | 90% | 20% | 60% | 30% | 10% | **320** |
| `both` | 40% | 80% | 0% | 70% | 30% | 20% | 458 |

Earlier `turns` run at k=10 with a stricter answerer prompt scored 28% — the
prompt fix (allow inference) + k=20 moved it to 40%.

> At n=10 per category the per-category deltas are within binomial noise
> (±~15pp); treat the category columns as directional, not precise. The robust
> signals: **distillation roughly halves context tokens** (646 → 320) and
> **lifts clean factual recall** (single-hop 70% → 90%).

### Full conv-26 (199 questions), facts mode, k=20

_(filled in from `datasets/run-conv26-facts/results.md` when the run completes;
larger per-category n gives sturdier numbers.)_

## Reference numbers (other layers, author-measured)

| System | LoCoMo overall | Mean tokens / retrieval | Source |
|---|---:|---:|---|
| Mem0 | 92.5% | ~6,956 | mem0.ai/research (data May 2026) |
| Zep | — (LongMemEval 71.2% w/ gpt-4o) | ~1,600 | getzep.com SOTA paper (Jan 2025) |

These are the **full** benchmark, the authors' **own end-to-end pipelines**, and
(for some) larger answer models. They are **not** apples-to-apples with the
single-conversation, gpt-4o-mini, strict-judge runs above.

## Why the gap? (honest analysis)

The ~40% here vs Mem0's 92.5% decomposes into **methodology differences** (which
inflate the apparent gap) and **genuine architecture gaps** (real work to do).

### Methodology — not a Mneme weakness

1. **Sample + model.** Above is one conversation (50–199 Q) with
   `gpt-4o-mini`. Mem0's 92.5 is the full 1,540-question set with its full
   pipeline and typically stronger models. Different denominator, different LLM.
2. **Binary LLM judge.** Our judge marks a *partially correct list* wrong
   (e.g. predicting "pottery, hiking" when the gold is "pottery, camping,
   painting, swimming"). LoCoMo's official scoring uses F1 / partial credit for
   these, which would lift multi-hop materially.
3. **Strict grounding earlier.** The first run forbade inference and scored
   28%; allowing the model to reason over retrieved snippets (as Mem0/Zep do)
   alone added +12pp. Prompt parity matters.

### Genuine retrieval gaps — the real backlog

4. **No reranking.** We feed the raw hybrid top-k straight to the answer model.
   Mem0/Zep rerank candidates; a cross-encoder rerank over the top-k would
   raise precision, especially for adversarial single-fact recall.
5. **No iterative / multi-hop retrieval.** Multi-hop questions need evidence
   gathered across several turns; a single top-k query misses the combination.
   This is the clearest gap (multi-hop 0–20%). Query decomposition + a second
   retrieval pass is the fix.
6. **No query expansion / decomposition.** Complex questions are embedded and
   matched as one string; expanding to sub-queries would help multi-hop and
   open-domain.
7. **Distillation loses specifics.** `facts` mode improves single-hop (atomic,
   clean facts embed well) but *drops* adversarial (10%) and open-domain (30%):
   the extractor generalizes away the buried specific detail ("a stained glass
   window") and the nuance reasoning questions need. A higher-granularity
   extraction prompt (or `both` mode, which keeps raw turns) mitigates this —
   Mem0 deliberately stores *both* raw and extracted memory.
8. **Recall depth.** k=20 over a 419-turn conversation can still miss the one
   relevant turn for an adversarial question. Higher k + rerank trades tokens
   for recall.

### What already works

- **Single-hop 70–90%** and **temporal 60–70%** are solid — core hybrid
  retrieval + bi-temporal stamping does its job.
- **Token efficiency**: facts mode at ~320 tokens/query is in Zep's ~1,600 /
  Mem0's ~7,000 range *while* being fully local and in-process.

## Prioritized next experiments

1. **Cross-encoder rerank** over the hybrid top-k (precision; helps adversarial).
2. **Iterative multi-hop retrieval** (decompose → retrieve → retrieve again).
3. **F1 / partial-credit judge** option to match LoCoMo's official scoring.
4. **Higher-granularity distillation prompt** + default `both` mode.
5. The full 1,540-question run (resume-driven across rate-limit windows) for a
   headline number with proper denominators.
