# Mneme — LoCoMo Benchmark Results

**Dataset:** LoCoMo-10 (Snap Research, ACL 2024) — 10 long multi-session
conversations, 1,986 questions across 5 categories.
**Memory layer:** Mneme (this repo), two-pass distillation (statements +
separate subject-attributed triples) with the knowledge-graph answer-context
supplement.
**Harness:** `benchmarks/Mneme.Benchmarks.LoCoMo` — deterministic
(`temperature=0`), resumable, per-question graded JSONL.

> **How to read this doc.** A memory-system benchmark score is only meaningful
> alongside the *configuration* that produced it. The same memory layer can look
> like 35% or 90% depending on the answerer model, how much context is retrieved,
> how lenient the grader is, and which question categories are counted. This doc
> reports every one of those knobs so the numbers are reproducible and
> comparable.

---

## Headline

| Configuration | Answerer | Retrieval depth | Tokens/query | LoCoMo cat 1–4 (Mem0-comparable) | All-5 overall |
|---|---|---:|---:|---:|---:|
| **Full match** (parity) | gpt-4o | top-200 | ~6,300 | **_(parity run in progress)_** | _(pending)_ |
| **Efficient** | gpt-4o-mini | top-25 | **~724** | **80.3%** | 68.8% |
| Mem0 (reported) | gpt-4o | top-200 | ~6,956 | 92.5% | — |

*(3-conversation subset earlier measured 90.4% in the full-match config; the
full 10-conversation parity number is being measured now and will replace the
placeholder above.)*

---

## The four knobs that move the score (and why)

Every published LoCoMo number bakes in these choices. Ours are stated explicitly.

### 1. Answerer model
The LLM that reads retrieved memories and writes the answer. Stronger model →
higher score, independent of the memory layer.
- **Efficient run:** `gpt-4o-mini` (cheap, fast).
- **Full-match run:** `gpt-4o` (the model Mem0's reference used).

### 2. Retrieval depth (context size)
How many memories are fed to the answerer per question. More context → higher
recall but far more tokens (= cost/latency).
- **Efficient run:** top-25 → **~724 tokens/query**.
- **Full-match run:** top-200 → ~6,300 tokens/query (Mem0 uses top-200, ~6,956
  tokens).
- The efficient config delivers **~9× less context** for a modest accuracy
  trade-off — a distinct, legitimate operating point.

### 3. Judge leniency
LoCoMo answers are free-text, so an LLM judge decides "correct". Mem0's public
J-score judge is **lenient**: partial credit (≥1 gold list item = correct),
paraphrases count, dates within ±14 days / durations ±50% match, semantic
overlap and same-referent accepted. We mirror it (`--judge mem0`,
`MemAlignedJudge`, re-expressed in our own wording, NOTICE-attributed). A strict
binary judge scores the *same predictions* several points lower — so judge
choice alone is a large source of cross-paper variance.

### 4. Category scope
LoCoMo has 5 categories. **Mem0 scores categories 1–4 and excludes adversarial
(category 5) entirely** (`CATEGORIES_TO_EVALUATE = [1,2,3,4]` in their runner).
The "Mem0-comparable" column applies the same exclusion. We *also* report
all-5 overall and the adversarial number separately, because we think trick
questions matter even if the standard comparison omits them.

---

## Efficient run — full detail (gpt-4o-mini, top-25, all 10 conversations)

- **Answerer / judge:** `gpt-4o-mini` (GitHub Models), Mem0-aligned answer
  procedure + Mem0-aligned lenient judge.
- **Retrieval:** hybrid semantic (`text-embedding-3-small`) + BM25, top-25,
  date-stamped snippets, KG subject-triple answer-context supplement.
- **Mean context:** ~724 tokens/query.

| Category | n | correct | accuracy | in Mem0 scope? |
|---|---:|---:|---:|:---:|
| single-hop | 841 | 727 | **86.4%** | ✅ |
| temporal | 321 | 233 | **72.6%** | ✅ |
| multi-hop | 282 | 220 | **78.0%** | ✅ |
| open-domain | 96 | 57 | **59.4%** | ✅ |
| adversarial | 446 | 130 | 29.1% | ❌ (excluded by Mem0) |
| **Mem0-comparable (1–4)** | **1,540** | **1,237** | **80.3%** | — |
| **All-5 overall** | **1,986** | **1,367** | **68.8%** | — |

---

## Full-match run — parity (gpt-4o, top-200, all 10 conversations)

_(Running now — this section will be filled with the per-category table when the
run completes. Config: gpt-4o answerer + judge, top-200 retrieval, same
two-pass-distilled DBs, Mem0-aligned answer procedure + judge, LoCoMo cat 1–4
scope. Earlier 3-conversation measurement in this config: 90.4% Mem0-comparable,
~6,297 tokens/query — vs Mem0's reported 92.5% at ~6,956 tokens.)_

---

## The alignment ladder — how the apparent "35% vs 92.5%" gap dissolved

Each row adds one alignment step to the *same memory layer*; only the
measurement/configuration changes.

| Step | Mem0-comparable |
|---|---:|
| strict judge, all-5 categories, gpt-4o-mini, top-25 | ~35% |
| + exclude adversarial (Mem0's scope) | ~53% |
| + lenient J-score judge (Mem0's grader) | 82.3%* |
| + Mem0's multi-step answer procedure | 83.6%* |
| + gpt-4o answerer + top-200 retrieval | 90.4%* |
| Mem0 (reported) | 92.5% |

*Measured on the 3-conversation subset; the full-10 efficient number is 80.3%
(gpt-4o-mini/top-25) and the full-10 parity number is in progress.

**Takeaway:** almost the entire original gap to Mem0 was measurement +
configuration (category scope, judge leniency, answer prompt, answerer model,
retrieval depth) — *not* memory-layer capability. On a like-for-like comparison
Mneme is competitive, and at the efficient operating point it reaches ~80% of
the in-scope questions at roughly one-tenth the retrieved-context cost.

---

## Caveats (stated honestly)

- **Adversarial vs recall depth.** Adversarial accuracy *drops* as retrieval
  depth grows (flooding the context with 200 memories reintroduces the
  attribution distractors the KG supplement suppresses at small k). It is out of
  the Mem0-comparable scope, but the tension between recall depth and
  attribution precision is real and documented, not a bug.
- **Distiller model.** These DBs were distilled with `gpt-4o-mini` even in the
  full-match run (only the answerer + judge are gpt-4o). Re-distilling with
  gpt-4o could close a residual point or two; it is not the dominant variable.
- **Reference numbers are the authors' own.** Mem0's 92.5% is measured by Mem0
  on their infrastructure; we reproduce their *methodology*, not their exact
  pipeline. For a fully controlled head-to-head, hold model + retrieval depth +
  judge + scope fixed and vary only the memory layer — which is what the
  full-match config does.
- **Judge is an LLM.** Even a deterministic (`temperature=0`) LLM judge has some
  grading noise; small differences (±1–2pp) between runs are within that noise.
