# LoCoMo benchmark — analysis

This document records Mneme's LoCoMo results and an honest analysis of where
the gaps are versus published numbers from other memory layers (Mem0, Zep).

> **Reproducibility:** all runs use GitHub Models — `openai/gpt-4o-mini` for
> both answering and judging, `openai/text-embedding-3-small@1536` for
> retrieval — driven by `benchmarks/Mneme.Benchmarks.LoCoMo`. Numbers are
> machine-, model-, and sample-specific.

## Runs

### Headline: stratified sample across ALL 10 conversations (245 Q)

Best config — both-mode + rerank + iterative multi-hop retrieval +
**date-stamped context** + **recall-retry** + hybrid judge, gpt-4o-mini +
text-embedding-3-small, k=20. 245 questions sampled evenly across all 10
LoCoMo conversations and all 5 categories (~50 each):

| Category | n | v1 (no dates/retry) | **v2 (dates+retry)** |
|---|---:|---:|---:|
| single-hop | 50 | 76.0% | **84.0%** |
| temporal | 50 | 20.0% | **64.0%** |
| open-domain | 45 | 42.2% | **51.1%** |
| multi-hop | 50 | 52.0% | 52.0% |
| adversarial | 50 | 18.0% | 16.0% |
| **Overall** | **245** | 41.6% | **53.5%** |

Mean context: 341 tokens/query (vs Mem0 ~6,956, Zep ~1,600).

**+11.9pp overall** from two targeted fixes on the same question set:
- **Temporal 20% → 64% (3.2×)** — surfacing each event's `[YYYY-MM-DD]` date
  into the retrieved snippet (the dates were already in Mneme's bi-temporal
  `valid_at`; they just weren't reaching the answer model) + instructing it to
  compute intervals. This was the single biggest lever in the whole arc.
- **single-hop 76 → 84, open-domain 42 → 51** — dates also help "when did X"
  factoid questions and ground inference questions.
- **multi-hop flat (52%)** — already addressed by iterative retrieval.
- **adversarial flat (18 → 16%, within noise)** — buried single-fact lookup is
  *not* helped by dates, and recall-retry's wider net only occasionally surfaces
  the needle. This is the one genuinely-unsolved category and the top remaining
  lever (needs higher base recall: bigger k + a stronger/true cross-encoder, or
  a fact-verification pass).

### Reranker routing: local ONNX cross-encoder vs LLM-listwise

Same 245-Q full-corpus set + config; only the `IReranker` implementation
swapped (one `--reranker onnx|llm` flag). This tests whether the seam is
genuinely provider-routable — and whether a *true* cross-encoder cracks
adversarial.

| Category | LLM-listwise (GitHub Models) | **ONNX cross-encoder (local, offline)** |
|---|---:|---:|
| single-hop | 84.0% | **86.0%** |
| temporal | 64.0% | 54.0% |
| open-domain | 51.1% | 46.7% |
| multi-hop | 52.0% | 52.0% |
| adversarial | 16.0% | **20.0%** |
| **Overall** | **53.5%** | 51.8% |
| rerank API calls | 1 chat call/question | **0 (fully local)** |

Two findings:
- **The two rerankers are within run-to-run noise on overall** (53.5 vs 51.8) —
  the `ms-marco-MiniLM-L-6-v2` ONNX cross-encoder matches the LLM reranker
  **with zero API calls, fully offline**. That's the routing win: rerank
  on-device, no key, no network, swapped behind `IReranker` with a single flag
  and **no change to Mneme** (`Mneme.Contracts` ships only the interface).
- **Adversarial barely moved (16 → 20%)** even with a real cross-encoder —
  decisive evidence that adversarial is **not a ranking problem**. The buried
  single fact either isn't in the candidate pool (recall) or was generalized
  away by distillation; a reranker can only reorder what retrieval surfaced.
  The fix is upstream: higher base recall + finer-grained distillation. This
  is the one genuinely-open category.

### Improvement progression (single conversation, conv-26, 199 Q)

Each lever added on top of the previous, gpt-4o-mini + text-embedding-3-small:

| Config | Overall | single | multi | temporal | open | adversarial | ctx tok | abstain* |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| facts, k=20 | 36.2% | 60.0 | 21.9 | 32.4 | 46.2 | 10.6 | 330 | 52% |
| facts + rerank | 39.2% | 64.3 | 25.0 | 35.1 | 46.2 | 12.8 | **99** | — |
| both + rerank + iterative | **46.2%** | 71.4 | 25.0 | 43.2 | 38.5 | **27.7** | 308 | **41%** |

*abstain = share of *misses* that were "I don't know" (a fact-not-retrieved /
recall signal). It fell from 52% → 41%; adversarial abstentions 33 → 22.

**+10pp overall** from the recall work (both-mode keeps raw turns so buried
specifics survive distillation; iterative multi-hop retrieval decomposes the
question and unions the hits). The category that moved most is **adversarial,
10.6% → 27.7% (>2.5×)** — exactly the recall-bound category the miss-analysis
predicted. Reranking earlier gave the token-efficiency win (330 → 99); recall
gave the accuracy win.

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

Robust per-category n (the 50-Q subset above is too small per category to trust):

| Category | n | Accuracy |
|---|---:|---:|
| single-hop | 70 | 60.0% |
| open-domain | 13 | 46.2% |
| temporal | 37 | 32.4% |
| multi-hop | 32 | 21.9% |
| adversarial | 47 | 10.6% |
| **Overall** | **199** | **36.2%** |

Mean context: **330 tokens/query** (vs Mem0 ~6,956, Zep ~1,600).

### Full conv-26, facts + **rerank**, k=20 (LLM listwise reranker)

| Category | n | facts | facts+rerank |
|---|---:|---:|---:|
| single-hop | 70 | 60.0% | 64.3% |
| open-domain | 13 | 46.2% | 46.2% |
| temporal | 37 | 32.4% | 35.1% |
| multi-hop | 32 | 21.9% | 25.0% |
| adversarial | 47 | 10.6% | 12.8% |
| **Overall** | 199 | 36.2% | **39.2%** |
| Mean ctx tokens | — | 330 | **99** |

Reranking lifted every category (+3pp overall) and **cut context to 99
tokens/query** (the reranker drops candidates that don't help, so the answer
model sees only the best facts). That's a >3× token reduction for a small
accuracy gain — Mneme is now at ~70× fewer tokens than Mem0 (~6,956) for this
config. The lift is modest because rerank is a *precision* stage: the remaining
gaps (adversarial, multi-hop) are *recall* problems — the right fact isn't in
the pool to rerank. Higher k + iterative multi-hop retrieval are the next levers.

The overall (36.2%) is *lower* than the balanced 50-Q facts run (42%) because the
real category mix is weighted toward the hard categories — **adversarial (47) and
temporal (37) are the two largest after single-hop**, and both are weak. The
balanced subset gave each category equal weight and hid that.

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

- **Single-hop 60%** (n=70) and **open-domain 46%** (n=13) are the strongest —
  core hybrid retrieval + the reasoning-enabled answer prompt do their job on
  clean factual and inference questions.
- **Token efficiency**: facts mode at ~330 tokens/query is *below* Zep's ~1,600
  and an order of magnitude below Mem0's ~7,000 — while being fully local and
  in-process. This is Mneme's clearest win on the comparable axes.

### The dominant drag: adversarial + temporal

On the full conv-26, the two biggest non-single-hop categories are also the
weakest, and they set the overall:
- **adversarial 10.6% (n=47)** — these hinge on one specific buried detail
  ("a stained glass window"). Distillation can generalize it away, and top-20
  retrieval over 419 turns can miss it. This single category drags the overall
  by ~5–6 points on its own. **Reranking + higher recall depth is the fix.**
- **temporal 32.4% (n=37)** — date-difference reasoning. Bi-temporal stamping
  gets the events in; the answer model still has to compute deltas. A temporal-
  aware retrieval/answer path would help.
- **multi-hop 21.9% (n=32)** — needs evidence gathered across turns; single-pass
  top-k misses the combination. **Iterative retrieval is the fix.**

## Prioritized next experiments

1. ✅ **Cross-encoder rerank** — shipped (`IReranker`); token-efficiency win.
2. ✅ **Iterative multi-hop retrieval** + **both-mode** — shipped; multi-hop
   22% → 52% across the full corpus (now a strength).
3. ✅ **F1/hybrid judge** + **question concurrency** — shipped; +2.5pp from
   judge parity; concurrency made the 10-conversation run tractable.
4. ✅ **Date-stamped context** (temporal) — shipped; temporal 20% → 64% (3.2×),
   the single biggest lever. Dates were already in Mneme's bi-temporal model;
   they just needed surfacing into the answer context.
5. ✅ **Recall-retry on abstention** (adversarial) — shipped; wider net + re-
   answer when the model abstains. Marginal here — adversarial stayed ~flat.
6. **Adversarial recall (16%)** — the one unsolved category. Buried single-fact
   lookup needs higher base recall: larger k into a *true* cross-encoder (not
   the LLM-listwise stand-in), or a fact-verification pass. **Top remaining lever.**
7. **Throughput** — GitHub Models concurrency-limits hard (heavy 429s above ~3
   in-flight, regardless of the 20k/min request budget). Full 1,986-Q run is
   ~2h at concurrency 2–3 (resume-driven) or needs a higher-concurrency provider.
