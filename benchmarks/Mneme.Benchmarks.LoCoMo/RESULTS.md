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
| **Full match** (parity) | gpt-4o | top-200 | ~6,254 | **89.6%** | 73.4% |
| **Efficient** | gpt-4o-mini | top-25 | **~724** | 80.3% | 68.8% |
| Mem0 (reported) | gpt-4o | top-200 | ~6,956 | 92.5% | — |

Both Mneme rows are the **full 10-conversation** LoCoMo-10 set (1,986 questions).
In the fully-matched configuration Mneme scores **89.6% vs Mem0's reported
92.5% — within ~3 points on 1,540 in-scope questions**, at comparable retrieved
context. The efficient configuration reaches **80.3% at ~9× less context**.

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

- **Answerer / judge:** `gpt-4o` (GitHub Models), Mem0-aligned answer procedure +
  Mem0-aligned lenient judge.
- **Retrieval:** hybrid semantic (`text-embedding-3-small`) + BM25, top-200,
  date-stamped snippets, KG subject-triple answer-context supplement.
- **Mean context:** ~6,254 tokens/query (comparable to Mem0's ~6,956).

| Category | n | correct | accuracy | in Mem0 scope? |
|---|---:|---:|---:|:---:|
| single-hop | 841 | 769 | **91.4%** | ✅ |
| temporal | 321 | 290 | **90.3%** | ✅ |
| multi-hop | 282 | 249 | **88.3%** | ✅ |
| open-domain | 96 | 72 | **75.0%** | ✅ |
| adversarial | 446 | 78 | 17.5% | ❌ (excluded by Mem0) |
| **Mem0-comparable (1–4)** | **1,540** | **1,380** | **89.6%** | — |
| **All-5 overall** | **1,986** | **1,458** | **73.4%** | — |

**89.6% vs Mem0's reported 92.5%** — within ~3pp on the full 1,540-question
in-scope set, at comparable retrieved context. Every controllable variable is
matched: answerer+judge model, retrieval depth, answer procedure, judge
leniency, and category scope.

---

## The alignment ladder — how the apparent "35% vs 92.5%" gap dissolved

Each row adds one alignment step to the *same memory layer*; only the
measurement/configuration changes. Full-10 rows are the complete 1,986-question
set; subset rows are marked.

| Step | Mem0-comparable |
|---|---:|
| strict judge, all-5 categories, gpt-4o-mini, top-25 | ~35%† |
| + exclude adversarial (Mem0's scope) | ~53%† |
| + lenient J-score judge (Mem0's grader) | 82.3%† |
| + Mem0's multi-step answer procedure | 83.6%† |
| **full-10 efficient** (gpt-4o-mini, top-25) | **80.3%** |
| **full-10 parity** (gpt-4o, top-200) | **89.6%** |
| Mem0 (reported) | 92.5% |

†Measured on a 3-conversation subset during iterative development; the two
**bold** rows are the full 10-conversation results.

**Takeaway:** almost the entire original gap to Mem0 was measurement +
configuration (category scope, judge leniency, answer prompt, answerer model,
retrieval depth) — *not* memory-layer capability. Fully matched, Mneme lands at
89.6% vs Mem0's 92.5% (~3pp, likely within distiller-model + judge-noise
margins). At the efficient operating point it answers ~80% of in-scope questions
at roughly one-tenth the retrieved-context cost.

---

## Caveats (stated honestly)

- **Adversarial vs recall depth.** Adversarial accuracy *drops* as retrieval
  depth grows — full-10 measured **29.1% at top-25 but 17.5% at top-200** —
  because flooding the context with 200 memories reintroduces the attribution
  distractors the KG supplement suppresses at small k. It is out of the
  Mem0-comparable scope, but the tension between recall depth and attribution
  precision is real and documented, not a bug.
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
