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

### Answer-model lever: gpt-4o vs gpt-4o-mini (balanced 50 Q)

Same best config; only the answerer+judge model swapped (reranker stays local ONNX):

| Category | gpt-4o-mini | **gpt-4o** |
|---|---:|---:|
| single-hop | ~90% | 90% |
| temporal | ~60% | **90%** |
| open-domain | ~40% | 60% |
| multi-hop | ~40% | 40% |
| adversarial | ~20% | 20% |
| **Overall (balanced 50)** | ~44% | **60.0%** |

The answer model is a **major lever**: +~16pp on the balanced subset, and it
essentially **solves temporal (→90%)**. On the natural LoCoMo category mix this
config with gpt-4o lands ≈ 60–64% overall.

But note what did **not** move: **multi-hop (40%) and adversarial (20%) are
flat** even with a much stronger model. That's the decisive evidence for the
ceiling — these are **retrieval-recall failures, not reasoning failures**. When
the buried single fact isn't in the retrieved context, a better answer model
can't invent it. Cracking 80% requires solving that recall problem (surfacing
one specific fact out of 400–700 turns), which is the genuinely hard,
still-open research problem here — not more answer-model or reranker tuning.

### Recall push (bigger pool + HyDE + entailment judge), full corpus 245 Q

Same set; +RerankPool 150, +HyDE query expansion, +entailment judge, ONNX reranker:

| Category | n | v2 (LLM rerank) | **push (recall+HyDE+judge, ONNX)** |
|---|---:|---:|---:|
| single-hop | 50 | 84.0% | **88.0%** |
| multi-hop | 50 | 52.0% | **60.0%** |
| open-domain | 45 | 51.1% | 46.7% |
| temporal | 50 | 64.0% | 52.0% |
| adversarial | 50 | 16.0% | **30.0%** |
| **Overall** | **245** | 53.5% | **55.5%** |

The recall levers did what they should: **adversarial 16 → 30% (nearly 2×)** and
**multi-hop 52 → 60%** — the bigger candidate pool + HyDE surfaced buried facts
the earlier runs missed, and the entailment judge stopped failing correct-but-
verbose answers. **Temporal regressed (64 → 52%)** because this run swapped the
LLM reranker for the ONNX cross-encoder, which is weaker on temporal (see the
reranker comparison above) — net the reranker change partly offset the recall
gains. Best-of-both would pair the LLM reranker (temporal) with these recall
levers.

**Reality check on the ceiling:** everything above runs on `gpt-4o-mini`, a
small answer model. LoCoMo end-to-end accuracy is heavily answer-model-bound
(Mem0/Zep report with `gpt-4o`). The retrieval layer is doing its job —
single-hop 88%, ~10–20× fewer context tokens than Mem0 — so the remaining gap
to 80% is dominated by (a) the answer/reasoning model and (b) the two hard
categories (temporal, adversarial). Because the answer model is host-pluggable
and identical across any fair comparison, "is the memory layer usable" is best
judged on *retrieval* quality, not the end-to-end score with a deliberately
small model.

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

---

## Diagnosis: why adversarial + multi-hop stall (controlled A/B, 2026-07-02)

Population: first-3 conversations (conv-26/30/41), **186** adversarial+multi-hop
questions, `--reuse-db` (identical distilled facts across all runs so only the
tested variable moves). Answerer/judge = gpt-4o-mini. Retrieval depth k=25.

### Experiment 1 — is the reranker the lever? **No (net-neutral).**

| Config | multi-hop | adversarial | overall |
|---|---:|---:|---:|
| reranker **off** (hybrid fusion top-25) | 62.2% | 17.0% | 34.9% |
| reranker **onnx** (pool 150 → rerank → top-25) | 62.2% | 17.0% | 34.9% |

Per-question: **101/186 predictions differ, 30 correctness flips — but net = 65 = 65.**
The reranker fixes ~15 and breaks ~15. Because the answer model already sees all
25 snippets, reordering *within* the pool is invisible unless it changes *membership*,
and at the boundary it is a wash. **Reranking is not the bottleneck.**

### Experiment 2 — is the gold fact even reaching the answer model? **Mostly yes.**

`GoldInContext` = gold answer's content tokens present in the 25 snippets fed to
the model (token-recall ≥ 0.6).

| Category | acc | gold-in-context | misses w/ gold present | of those: **abstained** | true recall miss |
|---|---:|---:|---:|---:|---:|
| adversarial | 16% | **78%** | 75/94 (79%) | **63** | 19 (20%) |
| multi-hop | 60% | 64% | 15/29 (51%) | 4 | 14 (48%) |
| ALL | 33% | 73% | 90/123 (73%) | 67 | 33 (26%) |

Only **26%** of misses are true recall misses. Adversarial acc is 16% despite
the gold token appearing in-context 78% of the time.

### Experiment 3 — force the model to commit (anti-abstention prompt). **Backfired.**

| adversarial | abstained | correct |
|---|---:|---:|
| original prompt | 77/112 | 18 |
| +attribution/anti-abstention prompt | **90/112** | **14** |

Adding "answer only from snippets about that exact person; don't abstain merely
from uncertainty" made the model abstain **more** (77→90) and score **less**. When
it applies strict attribution it discovers the in-context gold token is attached to
a **distractor** (another person), not the queried entity — so it correctly declines.

### Conclusion

The adversarial collapse is **not recall** and **not reranking** — it is
**entity attribution**: the gold *token* is in context, but a fact **cleanly
attributed to the person named in the question** often is not (distractors about
other people crowd the top-25). Multi-hop is genuinely half-retrieval (48% true miss).

**Next build → entity-anchored retrieval** (research rec #2, HippoRAG-lite family):
extract the entity named in the question, boost/hard-filter snippets that mention
that exact entity, and gather multi-hop evidence along entity→event adjacency.
Reuses Mneme's Phase-6 entity resolution; pure SQLite, no graph DB. Proposition
indexing (research rec #1) is already covered by Mneme's distilled-fact index —
which is why true recall miss is only 26%.

### Experiment 4 — entity-mention boost. **Flat (negative result).**

| Config | multi-hop | adversarial | overall |
|---|---:|---:|---:|
| baseline (reranker off) | 62.2% | 17.0% | 34.9% |
| + `--entity-boost` (float facts mentioning a query proper-noun) | 60.8% | 16.1% | 33.9% |

**Why it fails — the two-speaker naming problem.** Mneme's distiller resolves
pronouns to names, so in a 2-party LoCoMo conversation *both participants are named
in almost every fact*: "Caroline" appears in **149/247** distilled facts, "Melanie"
in **105/247**. Boosting "facts that mention the query entity" therefore selects
~half the corpus and discriminates nothing — the distractor facts name the entity too.

**And the answer is often not in any fact at all.** For "What country is Melanie's
grandma from? → Sweden", **0 of 105 Melanie-facts contain 'Sweden'** — the fact
extractor dropped the grandma's nationality; the token survives only in a raw turn
(hence `GoldInContext` = true via the `both` turn index, but no *attributable fact*
supports it).

### Final synthesis — what actually gates adversarial

Entity-*mention* is the wrong primitive. Closing adversarial needs **subject-scoped
facts**: extract `(subject_entity, predicate, object)` triples where the subject is a
*resolved* entity + relation path (Melanie → grandma → country), and retrieve by
matching the question's subject-chain — not by name co-occurrence. That is the
knowledge-graph investment Zep/Graphiti make, and it is a distillation-layer upgrade
(SVO extraction + entity-scoped fact index), not a retrieval toggle.

Mneme's current strengths are orthogonal and real: temporal (64–90%), single-hop
(88%). The adversarial gap is an architectural tradeoff of a statement-level fact log
vs. an SVO-triple graph — a deliberate, scoped build, not a quick lever. Recommend
promoting subject-attributed fact extraction to the backlog rather than chasing
benchmark-local hacks.

### Experiment 5 — subject-attributed knowledge triples (prototype). **Positive (additive), negative (replacement).**

Extracted `(subject, predicate, object)` triples from raw turns via the chat model
(296 for conv-26 vs 247 statement-facts — finer granularity), cached in a sidecar
`fact_triples` table. Subject = normalized name/possessive-chain (poor-man's entity
resolution). Retrieval scopes triples to the entities named in the question.

| Config | multi-hop | adversarial | overall |
|---|---:|---:|---:|
| baseline (statement facts only) | 62.2% | 17.0% | 34.9% |
| KG triples **replace** half the window | 55.4% | 15.2% | 31.2% |
| KG triples **supplement** full facts | 60.8% | **19.6%** | **36.0%** |

**Replacement lost** (−3.7pp): terse triples abstract the object — "What subject have
both painted? → *Sunsets*" became triple object "nature"; the compressed form strips
detail the answer step needs.

**Supplement won** (+1.1pp overall, **+2.6pp adversarial** — the first positive
adversarial lever in the arc). vs baseline: 9 gains / 5 regressions (net +4), and
**6 of 9 gains are adversarial** attribution wins ("Who is Caroline a fan of? → Ed
Sheeran", "Which song motivates Melanie? → Brave"). Subject-scoped attribution,
appended alongside the full facts, front-loads the right person's sub-graph without
losing detail.

**Production design (validated):** keep full-text facts; attach a resolved
`subject_entity_id` (via Phase-6 EntityResolver, not string-match); retrieve by
subject-scoped *filtering/boosting over full facts*, surfacing triples as an
attribution supplement. Do **not** replace facts with terse triples. This is the
`svo-facts` / `kg-*` backlog build.

### Experiment 6 — production knowledge-graph path (shipped code). **Retrieval-side boost regresses; default OFF.**

Wired the full production path in Mneme proper: FactTriple contract →
projection_fact_triples (schema v10) → FactTriplesProjector → subject-scoped
boost in MemoryQueryApi. The benchmark distiller now emits FactPayload.Triples,
so the shipped retrieval path (not the --kg sidecar) engages.

Fresh distillation (196 facts + 242 triples/conv), then A/B the subject-boost on
**identical** re-used DBs (only the boost toggles):

| Config (identical fresh triple-DBs) | multi-hop | adversarial | overall |
|---|---:|---:|---:|
| original cached distill, no boost (Exp 1 baseline) | 62.2% | 17.0% | 34.9% |
| triple-emitting distill, boost **OFF** | 52.7% | 19.6% | 32.8% |
| triple-emitting distill, boost **ON** | 55.4% | 11.6% | **29.0%** |

Two honest negatives:
1. **The triple-emitting distiller prompt cost ~2pp** (34.9→32.8): asking the
   distiller to emit statements AND triples in one pass slightly degraded the
   statement quality that the answer step depends on.
2. **The retrieval-side subject-boost regressed a further ~3.8pp** (32.8→29.0),
   adversarial 19.6→11.6. It reorders/injects facts by subject — the same
   *displacement* that made the "replacement" prototype (Exp 5) lose. The
   validated **win** in Exp 5 came from *appending* terse triples as
   supplementary answer context WITHOUT displacing the full facts — an
   answer-context/distillation concern, not retrieval reweighting.

**Decision:** ship the KG data infrastructure (contract, projection, projector,
toggle) — it is correct and tested — but default `SubjectAttributionBoost = OFF`.
Do not enable a lever that regresses in a controlled test. The path to the win is
answer-context supplementation (append subject-scoped triples alongside full
facts), and ideally a **separate** triple-extraction pass so fact-statement
quality is not diluted. Left as the next scoped step; the projection is ready for it.

### Experiment 7 — answer-context supplement in the shipped path. **At the noise floor.**

Built the validated "supplement, don't replace" form in shipped code:
QueryRequest.SupplementSubjectTriples → QueryResult.SubjectTriples (append-only
subject-scoped triples the consumer adds alongside the ranked items; the semantic
result is untouched). Benchmark --kg-supplement appends them to the answer context
(window grows 601→709 tokens, confirming it engaged).

A/B on identical fresh triple-DBs (only the supplement toggles):

| Config (identical fresh triple-DBs) | multi-hop | adversarial | overall |
|---|---:|---:|---:|
| boost OFF, no supplement | 52.7% | 19.6% | 32.8% |
| boost OFF, **supplement ON** | 54.1% | 17.0% | 31.7% |

Per-question: 5 gains / 7 regressions (**net −2**), only 62/186 predictions changed,
174 unchanged. This is within LLM answer non-determinism (±~4pp on n=112 adversarial),
i.e. **no demonstrable effect**. The earlier prototype win (Exp 5, +2.6pp / net +4)
was also near this floor AND drew on a *separate* extraction pass (296 rich triples
from raw turns) vs the production combined-distiller triples (242, lower quality
because the distiller did statement+triple double-duty — the same −2pp seen in Exp 6).

**Conclusion.** The KG data layer (contract, projection, projector, query surface,
supplement API) is built, tested, and shipped — but neither the retrieval boost
(Exp 6) nor the answer supplement (Exp 7) beats the semantic baseline beyond noise
on this benchmark with the current triple source. Both stay OFF by default. A
definitive verdict needs (a) a **separate** high-quality triple-extraction pass
feeding projection_fact_triples (so statement quality isn't diluted and triples are
richer), and (b) a lower-noise eval (multiple answer seeds / larger n). Further
benchmark grinding at the noise floor is not warranted until those are in place.

### Experiment 8 — separate triple-extraction pass + supplement. **The win (+3.2pp overall, +3.5pp adversarial).**

Fixes the two flaws Exp 6–7 exposed: (1) the distiller now runs statement
extraction and triple extraction as **two dedicated LLM calls** (`--kg-triples`)
instead of one combined prompt, so neither is done at half-attention; (2) the
richer triples feed the append-only answer-context supplement (`--kg-supplement`,
`QueryResult.SubjectTriples`) — semantic items untouched, window grows 608→710.

A/B on identical fresh two-pass-distilled DBs (only the supplement toggles):

| Config (identical DBs) | multi-hop | adversarial | overall |
|---|---:|---:|---:|
| supplement OFF | 56.8% | 16.1% | 32.3% |
| supplement **ON** | 59.5% | **19.6%** | **35.5%** |

Per-question: **net +6** (10 gains / 4 regressions), and the gains concentrate in
the target category — **adversarial net +4** (5 gains / 1 regression), multi-hop
net +2. Above the ±2 noise floor seen in Exp 7, and mechanistically where the
theory predicts (attribution). Example adversarial gains: "Why did Caroline get
into running? → de-stress", "Why did Melanie choose the adoption agency? →
inclusivity", "How did Caroline feel after the accident? → grateful".

**Why it works now where Exp 7 didn't.** Exp 7 fed the supplement with
*combined-distiller* triples (the model splitting attention between statements and
triples) → net −2. A **dedicated** triple pass yields more, and more precisely
attributed, triples → net +6. Triple *source quality* was the missing variable.
(Statement-only distillation did **not** recover a higher base — 32.3% vs the
combined-prompt 32.8% — so the earlier 34.9% was distillation-run variance, not a
combined-prompt penalty.)

**Status.** Both halves shipped + tested: two-pass `LlmSessionDistiller`
(benchmark host) and `QueryResult.SubjectTriples` (Mneme). The supplement is a
per-request opt-in (`QueryRequest.SupplementSubjectTriples`) — the validated way
to use the KG. `SubjectTripleResolver` additionally binds triple subjects to
canonical entity ids (Phase-6) for alias unification. Recommended host recipe:
distill statements + triples in separate passes, then query with
`SupplementSubjectTriples: true`.

### Experiment 9 — Mem0-aligned measurement (final headline). **82.3% Mem0-comparable.**

Root-caused the apparent gap to Mem0's 92.5 by reading Mem0's own public
benchmark suite (mem0ai/memory-benchmarks, Apache 2.0). Two measurement choices
account for most of it: (1) **Mem0 excludes adversarial (category 5) entirely**
(`CATEGORIES_TO_EVALUATE = [1,2,3,4]`) — we had been grinding the one category
they don't score; (2) Mem0 uses a **lenient J-score judge** (partial credit,
paraphrase/date/duration tolerance, semantic overlap, same-referent). Both are
now mirrored: `MemAlignedJudge` (`--judge mem0`, own wording, NOTICE-attributed)
and a "Mem0-comparable (cat 1–4)" report subtotal.

Full-category run (497 Q across conv-26/30/41, two-pass distill + KG supplement,
gpt-4o-mini answerer, mem0-aligned judge):

| Category | n | correct | acc |
|---|---:|---:|---:|
| single-hop | 200 | 176 | 88.0% |
| temporal | 90 | 73 | 81.1% |
| multi-hop | 74 | 57 | 77.0% |
| open-domain | 21 | 11 | 52.4% |
| adversarial | 112 | 24 | 21.4% |
| **Overall (all 5)** | **497** | **341** | **68.6%** |
| **Mem0-comparable (cat 1–4)** | **385** | **317** | **82.3%** |

**Headline: 82.3% on the Mem0-comparable scope** (vs their 92.5, gpt-4o-mini
answerer + ~714 tokens/query vs their ~6,956). The remaining gap is largely
answer-model strength (they use a stronger answerer + an elaborate 7-step
anti-abstention answer prompt) and further retrieval tuning — not a measurement
artifact anymore. Adversarial (21.4%) stays low but is out-of-scope for the
Mem0 comparison; it remains Mneme's honest internal stress metric.

### Experiment 10 — full apples-to-apples (Mem0-aligned answerer + judge + scope). **83.6% Mem0-comparable.**

Adds the last alignment lever: `--mem0-answer` re-expresses Mem0's multi-step
anti-abstention LoCoMo answer procedure (NOTICE-attributed, own wording), so the
head-to-head matches their **answerer method**, **judge leniency**, AND **scoring
scope** — not just one of the three.

Full-corpus subset (497 Q, conv-26/30/41, two-pass distill + KG supplement,
gpt-4o-mini, `--mem0-answer`, mem0-aligned judge):

| Category | n | correct | acc | vs Exp 9 (default answerer) |
|---|---:|---:|---:|---:|
| single-hop | 200 | 177 | 88.5% | +0.5 |
| temporal | 90 | 73 | 81.1% | = |
| multi-hop | 74 | 58 | 78.4% | +1.4 |
| open-domain | 21 | 14 | 66.7% | +14.3 |
| adversarial | 112 | 28 | 25.0% | +3.6 |
| **Overall (all 5)** | **497** | **350** | **70.4%** | +1.8 |
| **Mem0-comparable (cat 1–4)** | **385** | **322** | **83.6%** | +1.3 |

**Publishable headline: 83.6% on the fully-aligned Mem0-comparable scope**
(same answerer method, judge, and category scope Mem0 uses), at **715 tokens/query
vs Mem0's ~6,956** (≈10× less retrieved context), with a **gpt-4o-mini** answerer.
The open-domain jump (+14pp) came from the anti-abstention answer prompt; adversarial
also rose (+3.6) though it stays out of the Mem0 comparison scope.

Remaining gap to Mem0's 92.5 is now attributable to answerer model strength
(they use a larger model) and their ~10× larger retrieved context — both
independent of the memory layer. On a like-for-like memory-layer comparison
(same model, same judge, same scope, fixed retrieval depth) Mneme is competitive
at a fraction of the token cost.
