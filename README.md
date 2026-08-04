# Mneme

> *Μνήμη — Greek muse of memory, mother of the muses.*

**A local-first, .NET-native chronological memory substrate for AI agents.**

Mneme is what your agent reaches for when it needs to know what was
**decided**, what was **tried**, and what was **learned** — across sessions,
across workstreams, across time. Not a wiki. Not a vector store. Not a chat
log. A *substrate* that other software calls into, and that quietly compresses
everything an agent saw into useful, queryable, point-in-time-correct
knowledge.

[![CI](https://github.com/jacobmsft/mneme/actions/workflows/ci.yml/badge.svg)](https://github.com/jacobmsft/mneme/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)

```
                  ┌──────────────────────────────────────────────────────┐
                  │                Your agent host                       │
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
        │      memory_events (append-only, bi-temporal)                   │
        │  Evidence | Fact | Decision | Hypothesis | Goal | Action | Outcome
        │  projections · FTS5 + BM25 + semantic + recency retrieval        │
        │  HITL curation · capability-checked query API · MCP server       │
        └─────────────────────────────────────────────────────────────────┘
```

## Why Mneme

- **Bi-temporal, append-only event log** of seven epistemic categories
  (Evidence, Fact, Decision, Hypothesis, Goal, Action, Outcome) on plain
  SQLite — auditable, rebuildable, point-in-time-correct.
- **Bring your own LLM (or none).** The model is *host-supplied* via the
  `ISessionDistiller` / `IDistiller` seams. Mneme itself has **zero** LLM
  dependency — local Llama, OpenAI, Anthropic, Azure, on-device, or a stub.
- **Proactive context compression.** Instead of handing an agent a giant raw
  dump, Mneme distills sessions into a small, decision-useful synthesis.
- **Capability-checked queries** with workstream isolation and point-in-time
  (`AsOf`) lookups — no raw-SQL escape hatch on the public surface.
- **Human-in-the-loop curation is first-class:** `amend`, `annotate`, `pin`,
  `demote`, `revert`, all as append-only events with stale-state guards.
- **Speaks MCP.** Any Copilot / Claude / Cursor client can call Mneme through
  community-vocabulary tools (`remember`, `query`, `distill_session`, …).
- **Local-first.** Everything is one SQLite file; optional append-only
  snapshot sync, no last-write-wins conflict resolution.

On the [LoCoMo](https://github.com/snap-research/locomo) long-conversation
memory benchmark, Mneme scores **within ~3 points of Mem0** on a fully-matched
apples-to-apples configuration — at comparable retrieved context — and reaches
**80%** at roughly one-tenth the token cost in an efficient configuration. See
[the benchmark results](benchmarks/Mneme.Benchmarks.LoCoMo/RESULTS.md).

## Install

Mneme targets **.NET 8+**. One package gives you the memory store (the
contracts are bundled in):

```pwsh
dotnet add package Mneme --prerelease
```

Optional add-ons:

```pwsh
dotnet add package Mneme.Agents.AI --prerelease   # Microsoft Agent Framework integration
dotnet tool install -g Mneme.Mcp --prerelease     # MCP server tool (command: mneme-mcp)
```

## Quickstart

```csharp
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;

var services = new ServiceCollection();
services.AddMneme(o =>
{
    o.WorkstreamId = "my-team-q3-2026";
    o.SqlitePath   = Path.Combine(AppContext.BaseDirectory, "data", "mneme.db");
    o.UserId       = "alice@contoso.com";
});

// Bring your own distiller (chat turns -> epistemic events). Any IChatClient works.
services.AddSingleton<ISessionDistiller>(_ => new MySessionDistiller(myChatClient));

await using var sp = services.BuildServiceProvider();
var agent = sp.GetRequiredService<IMemoryAgent>();
var query = sp.GetRequiredService<IMemoryQueryAPI>();
var token = sp.GetRequiredService<CapabilityToken>();

// 1. Distill the session tail since Mneme's last watermark into memory.
await agent.DistillSessionAsync(session, entriesSinceWatermark, token);

// 2. Ask a capability-checked, point-in-time-correct question later.
var result = await query.QueryAsync(
    new QueryRequest(new QuerySpec(new WorkstreamId("my-team-q3-2026"),
        FreeText: "what did we decide about the auth rollout?")), token);
```

**The one architectural rule:** *the host owns the chat log; Mneme owns the
interpretation.* Mneme never stores raw chat turns — it distills the entries
that accumulated since its persisted watermark, ingests the resulting events
with citations back to the source, and advances the watermark atomically.

Full walkthrough in **[USAGE.md](USAGE.md)** (ingest paths, reading, curation,
MCP, MAF, sidecar, cloud sync, capability scoping).

## Use it from an MCP client

Installed as a tool, Mneme is a stdio MCP server any MCP-capable client can
launch:

```jsonc
// e.g. an MCP client config
{
  "mcpServers": {
    "mneme": { "command": "mneme-mcp", "args": [] }
  }
}
```

It exposes `remember`, `query`, `distill_session`, `get_watermark`, and more.

## Documentation

| Doc | What it covers |
|---|---|
| [docs/INTEGRATION.md](docs/INTEGRATION.md) | **One-shot integration recipe** — add Mneme to a host in a single pass. |
| [USAGE.md](USAGE.md) | End-to-end host integration guide. |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Deep technical walkthrough + design rationale. |
| [docs/why-continuous-memory.md](docs/why-continuous-memory.md) | Primer: why agent memory matters and where Mneme fits. |
| [docs/PUBLISHING.md](docs/PUBLISHING.md) | Release + NuGet publishing runbook. |
| [docs/adr/](docs/adr/) | Architecture decision records. |
| [benchmarks/…/RESULTS.md](benchmarks/Mneme.Benchmarks.LoCoMo/RESULTS.md) | LoCoMo benchmark methodology + numbers. |
| [AGENTS.md](AGENTS.md) | Onboarding + conventions for AI coding agents. |

## Build from source

```pwsh
dotnet build Mneme.slnx
dotnet test  Mneme.slnx

# Run the end-to-end sample (no API key; uses a stub LLM)
dotnet run --project samples/Mneme.Samples.AgentHost
```

## Packages

| Package | Kind | Description |
|---|---|---|
| `Mneme` | library | Core memory substrate (SQLite-backed). Bundles the BCL-only `Mneme.Contracts` surface. |
| `Mneme.Agents.AI` | library | Microsoft Agent Framework integration (`MnemeContextProvider`). |
| `Mneme.Mcp` | .NET tool | MCP server (`mneme-mcp`). |

## Status

Pre-1.0. Phases 0–12 implemented; the public API may change between minor
versions until 1.0. Semantic retrieval ships today via a brute-force cosine
index + hybrid fusion; a `sqlite-vec`-backed index for million-vector corpora
is deferred until that extension reaches 1.0.

Phases 13 (multi-agent shared workstreams) and 14 (offline "dreaming"
consolidation) are designed but not yet built — see
[ADR-0004](docs/adr/0004-multi-agent-and-dreaming.md), which also records the
privacy/compliance constraints for the single-user → multi-user shift.

## License

[Apache License 2.0](LICENSE). Mneme contains prompt templates and a benchmark
grading methodology adapted (in Mneme's own wording) from
[getzep/graphiti](https://github.com/getzep/graphiti) and
[mem0ai/memory-benchmarks](https://github.com/mem0ai/memory-benchmarks), both
Apache 2.0. See [NOTICE](NOTICE).

## Contributing

Issues for design feedback are very welcome. For conventions, build commands,
locked decisions, and the "don't do this" list, read [AGENTS.md](AGENTS.md);
for PR mechanics and code of conduct, [CONTRIBUTING.md](CONTRIBUTING.md).
