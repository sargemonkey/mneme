# ADR-0001: SQLite as the only embedded backend

- **Status:** Accepted
- **Date:** 2026-06-24 (decision predates the repo; recorded retroactively)
- **Deciders:** jacobmsft

## Context and Problem Statement

Mneme needs a durable substrate for a bi-temporal, append-only event log
plus derived projections, graph traversal (entity / fact relationships),
full-text search, and — eventually — vector search. It must run
**in-process** inside arbitrary .NET 8 hosts (CLI agents, desktop apps,
headless services) with no external process to deploy or operate. The
question is which storage engine(s) to commit to.

## Decision Drivers

- **Local-first, zero-ops.** The substrate must ship as a NuGet library
  with no sidecar process, no server to install, no container to run.
- **One file, portable.** A workstream's entire state should be a single
  file that can be copied, backed up, or exported (GDPR Article 20).
- **Covers all access patterns at our scale.** Bi-temporal queries,
  graph traversal, FTS, and later vectors must all be expressible
  against the same store without bolting on a second engine.
- **.NET-native.** First-class, well-maintained .NET driver.
- **Permissive licensing**, embeddable in commercial products.

## Considered Options

- **SQLite** (via `Microsoft.Data.Sqlite`), with FTS5 now and `sqlite-vec`
  later.
- **Neo4j** (graph-native).
- **KuzuDB** (embedded graph database).
- **Marten** (document + event-sourcing on PostgreSQL).
- **A polyglot stack** (e.g., SQLite for the log + a separate vector DB).

## Decision Outcome

Chosen option: **SQLite as the only embedded backend.**

`plans/research-zep-sqlite-deepdive.md §3` walks Graphiti's actual
Neo4j schema and demonstrates that the bi-temporal model, the graph
edges, FTS, and vector columns all translate to SQLite tables and
indexes, and that at Mneme's expected scale (single-user / single-team
workstreams, not internet-scale graphs) SQLite's traversal performance
is more than adequate. The append-only event log + rebuildable
projections pattern means we never need server-side graph algorithms at
write time.

### Consequences

- Good: zero-ops, single-file, fully embeddable, one engine to reason
  about, trivial backup/export, no network boundary.
- Good: WAL mode gives us concurrent readers + a single writer, which
  matches the "one host process per database file" model.
- Bad: no built-in distributed scale-out. Accepted — Mneme is
  local-first; the sidecar (Phase 9) and snapshot sync (Phase 10) cover
  the multi-process and multi-device cases without a server DB.
- Bad: vector search waits on `sqlite-vec` reaching v1 (Phase 11 is
  blocked on this). Accepted — FTS5 + structured queries are sufficient
  for v1 per the locked decision.

## Pros and Cons of the Options

### SQLite

- Good: embedded, single-file, zero-ops, first-class .NET driver,
  FTS5 built in, `sqlite-vec` on the roadmap, public-domain licensing.
- Bad: not horizontally scalable; vector extension still maturing.

### Neo4j

- Good: graph-native traversal, mature.
- Bad: a **separate server process** — fatal for a local-first,
  in-process .NET library. Operational + licensing weight.

### KuzuDB

- Good: embedded graph, columnar, fast.
- Bad: young, smaller ecosystem, no equivalent FTS/vector story in one
  engine; would still need SQLite alongside it. Archived as an option.

### Marten (PostgreSQL)

- Good: excellent .NET event-sourcing story.
- Bad: requires a PostgreSQL server. Same local-first disqualifier as
  Neo4j.

### Polyglot stack

- Good: each engine optimal for its access pattern.
- Bad: two stores to keep consistent, two deployment surfaces, two
  failure modes. Violates the zero-ops, single-file driver.

## More Information

- `plans/research-zep-sqlite-deepdive.md` §3 — SQL patterns + DDL
  blueprint, the proof that SQLite suffices.
- `plans/research-existing-systems.md` — the 19-system survey.
- `AGENTS.md` → "Locked decisions" → *SQLite as the only embedded
  backend*.
- `ARCHITECTURE.md` §5 — the live schema (v8).
