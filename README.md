# Mneme

> *Μνήμη — Greek muse of memory, mother of the muses.*

**Mneme is a local-first, .NET-native chronological memory substrate for AI agents.**

It is the thing your coding agent reaches for when it needs to know what was
decided, what was tried, what was learned — across sessions, across workstreams,
across time. Not a wiki. Not a vector store. Not a chat history. A *substrate*
that other software calls into and that quietly compresses everything an agent
sees into useful, queryable, point-in-time-correct knowledge.

**Status:** Design and planning phase. Schema and architecture are locked
(see [`plans/`](plans/)); no code yet. Open for design feedback; pre-alpha.

---

## What Mneme is (technically)

- A **bi-temporal knowledge graph** with seven epistemic categories
  (Evidence, Facts, Decisions, Hypotheses, Goals, plus experimental
  Actions and Outcomes), stored as an append-only event log on SQLite
  with derived projections.
- A **distillation pipeline** powered by a pluggable LLM that turns raw
  evidence into compressed, decision-useful synthesis — the agent's
  primary job is to *reduce* what other agents have to read, not just
  store more.
- A **capability-checked query API** (`IMemoryQueryAPI`) with strict
  workstream isolation; no raw SQL escape.
- A **conservative entity-resolution policy**: deterministic-key
  auto-merge only; LLM proposes, human confirms. Stricter than most
  agent-memory frameworks on purpose.
- **Content revocation**: immutable metadata + revocable artifact blobs.
  Retention forever + legal/privacy revocation, simultaneously.
- **Idempotent append-only sync** (ULID event IDs; no last-write-wins).
- **MCP server interface** alongside the .NET API, so any ACP /
  Copilot / Claude / Cursor client can query Mneme via standard MCP tools.

## What Mneme is *not*

- Not a wiki, doc tool, or note-taking app — users don't browse Mneme,
  they benefit from it via other software.
- Not a vendor of an LLM — bring your own (local llama, OpenAI,
  Anthropic, Azure, etc.).
- Not a cloud service — local-first; optional snapshot sync to S3-compatible
  storage.
- Not Graphiti / Mem0 / Letta / Zep — see
  [`plans/research-existing-systems.md`](plans/research-existing-systems.md)
  for why those didn't fit (short answer: all Python/TypeScript-first; no
  native .NET embedded library exists, until now).

## Why a separate project?

Mneme started as the memory subsystem inside
[MuxiMuxi](https://github.com/sargeMonkey/muximuxi), an AI cockpit for
engineering. Three things made it worth lifting out:

1. **The contracts are general.** Any agent host needs the same primitives.
2. **The substrate is reusable.** Memory shouldn't be tied to one cockpit.
3. **The design benefits from outside-in pressure.** Other consumers will
   stress-test assumptions that one cockpit can't.

## Project structure

```
mneme/
├── src/
│   ├── Mneme.Contracts/         # Interfaces + DTOs — NuGet-shippable
│   ├── Mneme/                   # The memory agent implementation
│   └── Mneme.Mcp/               # MCP server wrapper (exposes Mneme as MCP tools)
├── tests/
│   ├── Mneme.Contracts.Tests/
│   └── Mneme.Tests/
├── docs/                        # Architecture, schema, ADRs (forthcoming)
└── plans/                       # Planning + research artifacts
    ├── plan.md                  # 11-phase build plan, full v3+ scope
    ├── research-existing-systems.md       # Survey of 19 systems
    └── research-zep-sqlite-deepdive.md    # Why SQLite is enough (with proofs)
```

## Status & roadmap

| Phase | Status | Outcome |
|---|---|---|
| 0. Contracts (interfaces + DTOs) | not started | Shippable `Mneme.Contracts` NuGet |
| 1. Event log + SQLite schema | not started | Append-only, idempotent ingest |
| 2. Classification + revocation | not started | Labels stored; artifacts tombstone-able |
| 3. Projections (facts/decisions/hypotheses/goals/entities) | not started | Current-state views, rebuildable |
| 4. Temporal graph + capability-checked query API | not started | Point-in-time queries; workstream isolation |
| 5. Distillation pipeline (extract + bundle + rationale) | not started | **Primary value** — context compression |
| 6. Conservative entity resolution | not started | Deterministic auto-merge + LLM-propose pipeline |
| 7. Outcome closure | not started | Action → Decision linkage |
| 8. MCP server interface | not started | Exposes memory to any ACP-compatible agent |
| 9. Sidecar deployment | not started | Separate-process gRPC option |
| 10. Cloud snapshot sync | not started | Idempotent append-only merge to S3 |
| 11. (v2) Autonomous capture + vector search | not started | Heuristic capture + sqlite-vec |

Full plan: [`plans/plan.md`](plans/plan.md).

## Design pressure-test

If you think Mneme should not exist (e.g., "Graphiti does this; just use
that"), read
[`plans/research-zep-sqlite-deepdive.md`](plans/research-zep-sqlite-deepdive.md).
That report goes through Graphiti's actual source code and demonstrates
that (a) its architecture is schema + prompts + orchestration on top of
Neo4j, (b) the schema translates directly to SQLite, (c) the prompts are
Apache 2.0 and portable, and (d) the Python sidecar tax is incompatible
with local-first .NET desktop products. Mneme is what falls out when you
take Graphiti's good ideas and reimplement them where .NET shops can use
them.

## License

[Apache License 2.0](LICENSE).

Mneme contains prompt templates ported from
[getzep/graphiti](https://github.com/getzep/graphiti) (also Apache 2.0).
See [`NOTICE`](NOTICE) for attribution.

## Contributing

Pre-alpha. Issues for design feedback are very welcome; PRs against an
empty codebase less so. Open an issue to discuss anything in [`plans/`](plans/).
