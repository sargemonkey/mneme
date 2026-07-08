# Mneme

> *Μνήμη — Greek muse of memory, mother of the muses.*

**A local-first, .NET-native chronological memory substrate for AI agents.**

Mneme is the thing your agent reaches for when it needs to know what was
decided, what was tried, and what was learned — across sessions, across
workstreams, across time. Not a wiki. Not a vector store. Not a chat
history. A *substrate* that other software calls into and that quietly
compresses everything an agent saw into useful, queryable, point-in-time-
correct knowledge.

```
                  ┌──────────────────────────────────────────────────────┐
                  │                Your agent host                       │
                  │                                                      │
                  │   chat log (host-owned, full history, source of truth)
                  │       │                                  ▲           │
                  │       │ entries since last watermark     │ bundle    │
                  │       ▼                                  │ injected  │
                  │   ┌─────────────────────┐    ┌────────────────────┐  │
                  │   │ DistillSessionAsync │    │ DistillAsync(read) │  │
                  │   └──────────┬──────────┘    └─────────▲──────────┘  │
                  └──────────────│─────────────────────────│─────────────┘
                                 ▼                         │
                       ┌────────────────────┐    ┌────────────────────┐
                       │ ISessionDistiller  │    │   IDistiller       │   ← host-supplied
                       │  (chat → events)   │    │ (events → bundle)  │     LLMs (or not)
                       └──────────┬─────────┘    └─────────▲──────────┘
                                  ▼                        │
        ┌─────────────────────────────────────────────────────────────────┐
        │                       Mneme                                     │
        │   ┌─────────────────────────────────────────────────────────┐   │
        │   │      memory_events (append-only, bi-temporal)           │   │
        │   │  Evidence | Fact | Decision | Hypothesis | Goal |       │   │
        │   │  Action   | Outcome — every event carries a Citation    │   │
        │   └─────────────────────────────────────────────────────────┘   │
        │   projections (facts, decisions, chains, entities)              │
        │   FTS5 + adaptive BM25 + recency + curation-weighted retrieval  │
        │   HITL curation (amend / annotate / pin / demote / revert)      │
        │   Capability-checked IMemoryQueryAPI + MCP server               │
        └─────────────────────────────────────────────────────────────────┘
```

**Status:** Phases 0 – 10 + 8.5 shipped. 316 tests passing. Pre-alpha but functional.
Phase 11 (sqlite-vec) blocked upstream.

---

## What Mneme is

- A **bi-temporal append-only event log** of seven epistemic categories
  (Evidence, Fact, Decision, Hypothesis, Goal, Action, Outcome) on SQLite.
- A **distillation pipeline** that turns raw session chat into a small,
  decision-useful synthesis. The LLM is **host-supplied** via the
  `ISessionDistiller` (ingest side) and `IDistiller` (read side)
  interfaces; Mneme itself has zero LLM dependency.
- A **capability-checked query API** with workstream isolation and
  point-in-time (`AsOf`) queries — no raw-SQL escape hatch.
- A **conservative entity-resolution policy**: deterministic UUID5
  auto-merge; embedding ≥ 0.95 cosine auto-merge; LLM-judgment merges
  go through a propose-then-confirm pipeline.
- **HITL curation as a first-class surface** (`amend` / `annotate` /
  `pin` / `demote` / `revert`) with stale-state guards.
- **MCP server** alongside the .NET API: any Copilot / Claude / Cursor
  client can call Mneme through community-vocab tools (`remember`,
  `query`, `distill_session`, `get_watermark`, …).
- **MAF integration** (`Mneme.Agents.AI`) — five lines of DI and a
  Microsoft Agent Framework agent reads its prior context from Mneme.
- **Optional cloud sync** of append-only snapshot batches; no
  last-write-wins, no conflict resolution required.

## What Mneme is *not*

- Not a wiki, doc tool, or note-taking app. End users don't browse
  Mneme; they benefit from it via other software.
- Not a vendor of an LLM. Bring your own (local llama, OpenAI,
  Anthropic, Azure, on-device, none).
- Not a cloud service. Local-first; optional snapshot sync.
- Not Graphiti / Mem0 / Letta / Zep — see
  [`plans/research-existing-systems.md`](plans/research-existing-systems.md)
  for why those didn't fit (short answer: all Python/TS-first; no native
  .NET embedded library existed).

## The one big architectural rule

**The host owns the chat log; Mneme owns the interpretation.** Mneme
never stores raw chat turns. Periodically the host calls
`IMemoryAgent.DistillSessionAsync(session, entries, capability)` with
the entries that have accumulated since Mneme's persisted watermark for
that session. Mneme runs the host's distiller, ingests the produced
epistemic events with `Citation.SessionRange` stamps pointing back at
the source entries, and atomically advances the watermark. Re-
distillation is just calling again with a lower watermark; new events
sit alongside old ones (append-only). This is locked in
[`AGENTS.md`](AGENTS.md) and not subject to relitigation.

## Project layout

```
mneme/
├── README.md                   ← you are here
├── ARCHITECTURE.md             ← deep technical walkthrough
├── USAGE.md                    ← end-to-end howto for hosts
├── AGENTS.md                   ← onboarding doc for AI coding agents
├── CHANGELOG.md
├── CONTRIBUTING.md
├── Mneme.slnx                  ← solution (XML format)
├── src/
│   ├── Mneme.Contracts/        ← BCL-only interfaces + DTOs
│   ├── Mneme/                  ← storage, ingest, projections, query, etc.
│   ├── Mneme.Mcp/              ← stdio MCP server
│   ├── Mneme.Agents.AI/        ← Microsoft Agent Framework integration
│   ├── Mneme.Sidecar/          ← HTTP sidecar host + Dockerfile
│   ├── Mneme.Cli/              ← command-line front-end
│   ├── Mneme.Studio/           ← Blazor Server UI
│   ├── Mneme.Studio.Desktop/   ← Photino-wrapped native window
│   └── Mneme.Studio.Electron/  ← pure-desktop Electron app
├── samples/
│   └── Mneme.Samples.AgentHost/  ← canonical end-to-end pattern
├── benchmarks/
│   └── Mneme.Benchmarks/         ← LoCoMo/LongMemEval harness
├── tests/
│   ├── Mneme.Contracts.Tests/    (136 tests)
│   ├── Mneme.Tests/              (177 tests)
│   └── Mneme.Agents.AI.Tests/    (3 tests)
└── plans/                        ← design docs, research, backlog
```

## Quick start

```pwsh
# Restore + build + test
dotnet build Mneme.slnx
dotnet test  Mneme.slnx

# Run the end-to-end sample (no API key needed; uses a stub LLM)
cd samples/Mneme.Samples.AgentHost
dotnet run
```

See [USAGE.md](USAGE.md) for the full howto, [ARCHITECTURE.md](ARCHITECTURE.md)
for the design walkthrough.

## Status

| Phase | Status | What |
|---|---|---|
| 0 — Contracts | ✅ | BCL-only `Mneme.Contracts` |
| 1 — Event log + ingest | ✅ | SQLite WAL, secret redactor, <50ms p99 |
| 2 — Classification + revocation | ✅ | `AddMneme(opts=>{})` DI helper |
| 3 — Projections + FTS5 | ✅ | facts/decisions/goals/hypotheses + adaptive-BM25 |
| 4 — Capability-checked query API | ✅ | Explain + AsOf bi-temporal lookup |
| 4.5 — Benchmarks | ✅ | Full LoCoMo harness: hybrid retrieval, GitHub-Models runner, resume/CSV/MD |
| 5 — Distillation | ✅ | `IDistiller` (read) + `ISessionDistiller` (ingest) |
| 6 — Entity resolution | ✅ | 3-tier (UUID5 / cosine ≥0.95 / LLM-propose) |
| 7 — Outcome closure | ✅ | `DecisionChainsProjector` + `FeedbackLearner` |
| 7.5 — HITL curation | ✅ | `IMemoryCurator` w/ amend/annotate/pin/demote/revert |
| 8 — MCP server | ✅ | stdio, community-vocab tools |
| 8.5 — MAF integration | ✅ | `MnemeContextProvider : AIContextProvider` |
| 9 — HTTP sidecar | ✅ | bearer-auth + Dockerfile |
| 10 — Cloud sync | ✅ | `ISyncStore` + `SyncEngine` |
| UI scaffold | ✅ | Studio (Blazor) + Desktop (Photino) + Electron |
| 11 — sqlite-vec | ⏸ blocked | Waiting on sqlite-vec v1; brute-force cosine `VectorIndex` shipped as the bridge |
| 12 — Subject-attributed KG | ✅ | `FactTriple` + `projection_fact_triples` + subject-scoped query + append-only `SubjectTriples` supplement + `SubjectTripleResolver`. **Validated: separate triple pass + answer-context supplement = +3.2pp overall / +3.5pp adversarial** (`benchmarks/.../ANALYSIS.md` Exp 8) |

**Verification**: `dotnet test Mneme.slnx` → 341/341.

## Background reading

New to agent memory? Start with
[`docs/why-continuous-memory.md`](docs/why-continuous-memory.md) — a
primer on why continuous memory matters, the four core memory types,
how memory updates, and where Mneme fits among Mem0 / Letta / Zep and
others. For a deeper field survey, see
[`plans/memory-systems-primer.md`](plans/memory-systems-primer.md).

## License

[Apache License 2.0](LICENSE). Mneme contains prompt templates ported
from [getzep/graphiti](https://github.com/getzep/graphiti) (also
Apache 2.0). See [`NOTICE`](NOTICE).

## Contributing

Pre-alpha. Issues for design feedback are very welcome. For day-to-day
conventions, build commands, locked decisions, and the "don't do this"
list, read [`AGENTS.md`](AGENTS.md). For code-of-conduct and PR
mechanics, see [`CONTRIBUTING.md`](CONTRIBUTING.md).
