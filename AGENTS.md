# AGENTS.md — Onboarding for AI agents working on Mneme

> This is the canonical onboarding doc for any AI coding agent (Copilot,
> Claude, Codex, Aider, Cursor, etc.) picking up work on Mneme. Read it
> top to bottom before making any change. If you only read one section,
> read **"Read order"** and **"Start here"** below.

## What you're working on

**Mneme** is a local-first, .NET 8 chronological memory substrate for AI
agents. The substrate is a **bi-temporal knowledge graph** of seven
epistemic categories (Evidence, Facts, Decisions, Hypotheses, Goals,
plus experimental Actions and Outcomes) backed by **SQLite as the event
log + projections**, fronted by a **capability-checked query API** and an
**MCP server**. The point of the project is **proactive context
compression** — a distillation pipeline that hands consuming agents a
small, decision-useful synthesis instead of a giant raw event dump.

The high-level "why this exists and why not just use Graphiti / Zep /
Mem0" answer is in [`README.md`](README.md). The technical "why
SQLite is sufficient" answer is in
[`plans/research-zep-sqlite-deepdive.md`](plans/research-zep-sqlite-deepdive.md).

Status: **design phase, no implementation code yet.** The scaffolding
(projects, license, NuGet metadata, planning docs) is in place. Your
job, most likely, is to land **Phase 0 contracts** as the first PR.

## Read order

Read in this order. Stop after each file and decide if the next one is
still needed for your task.

1. **[`README.md`](README.md)** — what Mneme is, what it isn't, roadmap.
2. **This file** (`AGENTS.md`) — conventions, locked decisions, how to ship.
3. **[`plans/backlog.md`](plans/backlog.md)** — dependency-ordered task
   list. Find the first item with no unmet dependencies; that's almost
   certainly your task.
4. **[`plans/plan.md`](plans/plan.md)** — the long-form design (data
   model, ingest flow, distillation, entity resolution, etc.). The
   backlog is derived from this; read the plan section relevant to your
   task in depth.
5. **[`plans/research-zep-sqlite-deepdive.md`](plans/research-zep-sqlite-deepdive.md)**
   — read **§3** (SQL patterns + DDL blueprint) before writing any
   storage code; it contains literal schemas to translate. Read **§5**
   (Graphiti prompt provenance) before porting any prompt.
6. **[`plans/research-existing-systems.md`](plans/research-existing-systems.md)**
   — skim if you're tempted to suggest "let's just use X instead." 19
   alternatives were evaluated; the answer is in there.
7. **[`plans/research-design-lessons.md`](plans/research-design-lessons.md)**
   — source-level deep dives of Mem0, Letta, Cognee, MCP ecosystem,
   and MS Agent Framework + KM², with cross-cutting design patterns
   and an explicit stress-test of Mneme's plan. **§3 lists the design
   ideas adopted** into this codebase; **§5 maps each new backlog
   task to its source justification.** Read §2 sub-sections for any
   framework you're studying; §3 before designing the corresponding
   surface; §4 before defending a contested design choice.
8. **[`plans/memory-systems-primer.md`](plans/memory-systems-primer.md)**
   — vocabulary + mental model for agent memory systems. Read first
   if you're new to the field. The §14 comparison matrix is the
   reference for evaluating any new memory product against Mneme.
9. **[`plans/consumer-architecture-reference.md`](plans/consumer-architecture-reference.md)**
   — only if you're touching the contracts surface and want to
   understand how a consumer (MuxiMuxi cockpit) wires Mneme in.

## Start here

If you are the **first agent** to add code:

1. The repo is empty of source files on purpose. Don't add random files.
2. Your task is **Phase 0 — Contracts**. See `plans/backlog.md` "Phase 0".
3. Land contracts in `src/Mneme.Contracts/` as a series of small,
   reviewable commits — one interface or DTO per commit ideally.
4. Add a test in `tests/Mneme.Contracts.Tests/` for each public type
   (even if it's just "type exists and properties are settable") to
   prove the build + test pipeline runs.

If you are a **later agent**:

1. Run `git log --oneline -20` and read the most recent commits to see
   what just shipped.
2. Open `plans/backlog.md` and find the first task whose dependencies
   are all marked ✅.
3. If unsure, prefer **finishing an in-progress phase** over starting a
   new one.

## Build, test, run

This repo targets **.NET 8**. Any .NET SDK ≥ 8.0 works (we develop
against SDK 10; lower bound is 8). All commands are run from repo root.

```pwsh
# restore + build everything
dotnet build Mneme.slnx

# run all tests
dotnet test Mneme.slnx

# build a single project
dotnet build src/Mneme.Contracts/Mneme.Contracts.csproj

# pack the NuGet (Phase 0+ only; pre-Phase-0 there's no shippable code)
dotnet pack src/Mneme.Contracts/Mneme.Contracts.csproj -c Release -o ./artifacts
```

> **Solution file is `Mneme.slnx`** (the new XML solution format
> introduced in SDK 10). It works identically to `.sln` for build/test
> /pack commands.

**Warnings are errors.** `TreatWarningsAsErrors=true` in
`Directory.Build.props`. Don't merge if `dotnet build` produces any
warning. (`CS1591` is suppressed — you don't need XML doc comments on
every public member during early development, but do add them where
they materially help.)

**Nullable reference types are enabled** repo-wide. Don't disable.

## Code conventions

These are repo-wide via `.editorconfig` and `Directory.Build.props`:

- **File-scoped namespaces** (`namespace Mneme.Contracts;`).
- **Nullable** enabled; use `string?` not `[Nullable]` patterns.
- **`var` for built-in types** when the type is obvious; explicit type
  when it isn't.
- **`I` prefix on interfaces** (`IMemoryAgent`, not `MemoryAgent`).
- **`async` suffix on async methods.**
- **CommunityToolkit.Mvvm is for consumers, not Mneme.** Mneme has no
  UI; don't pull MVVM packages.
- **One public type per file** for anything non-trivial. Small related
  DTOs may share a file if it improves readability.
- **No `static using` for project namespaces.**
- **Records for immutable DTOs.** Classes for things with behavior.
- **`ulong` / `long` for timestamps** as Unix-ms epoch (not `DateTime`)
  on storage boundary; `DateTimeOffset` only in API surface for
  ergonomics.

## Architectural rules — do not violate

These are load-bearing. Changing any of them needs a written ADR in
`docs/adr/` and an issue discussion first.

1. **`Mneme.Contracts` depends on nothing but the .NET 8 BCL.** No
   SQLite, no MCP SDK, no Semantic Kernel, no JSON.NET, no anything.
   The contracts must be safe to put on any .NET 8 codebase's allowlist.
2. **`Mneme` depends on `Mneme.Contracts` + SQLite + (later) LLM
   provider abstractions.** Never on `Mneme.Mcp`.
3. **`Mneme.Mcp` depends on both.** The MCP server is a wrapper, not
   a peer.
4. **The event log is the source of truth.** Projections (facts table,
   decisions table, entities table) are derived and must be rebuildable
   from scratch by replaying `memory_events` end to end.
5. **Append-only event log.** No `UPDATE` and no `DELETE` on
   `memory_events`. Revocation tombstones the artifact blob in a
   sidecar table; metadata stays intact.
6. **Bi-temporal model.** Every fact carries four timestamps: `valid_at`,
   `invalid_at`, `created_at`, `expired_at`. See
   `research-zep-sqlite-deepdive.md §3.1` for the schema.
7. **Idempotency on `event_id` (ULID).** Re-ingesting the same event is
   a no-op, not a duplicate. This is non-negotiable for sync correctness.
8. **Capability tokens guard every query.** No raw-SQL escape hatch on
   the public API. Workstream isolation is enforced at the API layer,
   not by convention.
9. **Conservative entity resolution.** Auto-merge **only** on
   deterministic keys (email, GitHub ID, Linear ID, …). LLM-judgment
   merges go through a propose-then-confirm pipeline; they never
   auto-apply. Stricter than Graphiti on purpose.
10. **Pluggable LLM provider via `Microsoft.Extensions.AI.IChatClient`.**
    Mneme's distillation / classification / entity-resolution LLM
    calls go through `IChatClient` (from
    `Microsoft.Extensions.AI.Abstractions ≥ 10.4.0`). Never hardcode
    OpenAI / Anthropic / Azure. **`IChatClient` replaces the earlier
    Semantic Kernel plan** — see `research-design-lessons.md` §2.15
    + §4.7 for rationale (MAF dropped SK; `IChatClient` is now the
    .NET LLM abstraction). Mneme/Llm/ project depends only on
    `Microsoft.Extensions.AI.Abstractions`.
11. **No PII / secret storage.** Secret redactor runs **inline at
    ingest**. Classifier labels stay metadata-only; if a label says
    "contains secret", the redactor should have already removed it.
12. **Workstream scope on every event.** No global / cross-workstream
    queries unless the capability token explicitly grants it.
13. **HITL curation is first-class, not an afterthought.** Curation
    operations (`amend`, `annotate`, `pin`, `demote`, `split`,
    `merge`, `revert`) flow through `IMemoryCurator` with a
    `CurationCapability` token, separate from `IMemoryAgent`
    (ingest) and `IMemoryQueryAPI` (read). Every curation is an
    append-only event with a stale-state guard (Letta
    `core_memory_replace` pattern). **Never** mutate projections or
    artifacts in place; always append a curation event and let the
    projector apply it. See `plan.md` "Human-in-the-loop curation"
    section.

## Locked decisions — do not relitigate without an ADR

These have been argued through and have a written rationale. If you
think they're wrong, open an issue with new evidence; don't just
"refactor" them.

| Decision | Where it's argued | Short answer |
|---|---|---|
| SQLite as the only embedded backend | `research-zep-sqlite-deepdive.md` §3 | Bi-temporal + graph traversal + FTS + (later) vector all fit in SQLite at our scale. KuzuDB is archived; Neo4j is a process. |
| .NET 8 / not Python | `research-existing-systems.md` | Local-first, in-process embedding; no Python sidecar tax. |
| Build, not adopt Graphiti/Zep/Mem0 | `research-existing-systems.md` + `research-zep-sqlite-deepdive.md` | All Python-first; Graphiti is "schema+prompts on Neo4j" and the schema/prompts are portable under Apache 2.0. |
| Apache-2.0 | `LICENSE`, `NOTICE` | Compatible with porting Graphiti prompts (also Apache 2.0); permissive enough for commercial reuse. |
| Append-only + projections > graph DB | `plan.md` "Storage architecture" | Event log = audit + rebuildable; projections = fast reads. |
| Conservative entity resolution (three-tier) | `plan.md` "Entity resolution" + `research-design-lessons.md` §3.4 | Tier 1: deterministic UUID5 auto-merge. Tier 2: embedding ≥0.95 cosine. Tier 3: LLM-propose + human-confirm. Stricter than Graphiti. |
| Pluggable LLM via `IChatClient` | `research-design-lessons.md` §2.15 + §4.7 | `Microsoft.Extensions.AI.IChatClient` (≥10.4.0). **Supersedes earlier Semantic Kernel choice** — MAF dropped SK; `IChatClient` is now the .NET LLM abstraction. |
| Workstream isolation by default | `plan.md` "Capability tokens" | Cross-workstream requires explicit grant; not opt-out. |
| Sync ingest + async distillation split | `research-design-lessons.md` §3.2 + §4.2 | `Ingest` returns <50ms after WAL commit; distillation runs in a `DistillationJob` worker. Evidence: Mem0 v2→v3 dropped sync invalidation, +20 LoCoMo points. |
| **Host owns the chat log; Mneme owns the interpretation** (2026-06) | `samples/Mneme.Samples.AgentHost/README.md` | The host's session has a monotonically growing context buffer (chat turns, tool outputs, file reads, sub-agent results). Mneme **never stores raw turns**. Periodically the host calls `IMemoryAgent.DistillSessionAsync(session, entries, capability)` with the entries that accumulated since the persisted watermark; Mneme runs the host's `ISessionDistiller`, ingests the produced epistemic events with `Citation.SessionRange` stamps, and atomically advances the watermark. Re-distillation = same call with a lower watermark; new events sit alongside old ones (append-only). **Per-turn capture interfaces are forbidden** (deleted 2026-06: `ICapturePolicy`, `CaptureSession`, `RecentDuplicateFilter` — see commit log). The `Mneme.Agents.AI` MAF provider is read-only by design (no `InvokedAsync` capture pump). |
| MCP tool naming = community vocabulary | `research-design-lessons.md` §2.8 + §4.5 | MCP edge exposes `remember` / `query` / `distill` / `forget` / `list_recent`. .NET `IMemoryQueryAPI` keeps Mneme-native names. |
| MAF integration via `MessageAIContextProvider` | `research-design-lessons.md` §2.15 | No custom `IMemoryStore` interface — MAF's integration seam is `MessageAIContextProvider`. `Mneme.Agents.AI` ships a derived class. |
| Bi-temporal as Mneme's primary differentiator | `research-design-lessons.md` §4.8 + `memory-systems-primer.md` §7 | Two timestamps (`valid_at`, `recorded_at`); architecturally beats single-timestamp competitors on temporal benchmarks. Mneme must run LoCoMo + LongMemEval to verify. |
| No vector search in v1 | `plan.md` Phase 11 / `research-zep-sqlite-deepdive.md` §6 | sqlite-vec pre-v1 as of 2026-06; FTS5 + structured queries are enough for v1. |
| HITL curation as first-class | `plan.md` "Human-in-the-loop curation" + `backlog.md` Phase 7.5 | Strong curation API beyond confirm/revoke: `amend`, `annotate`, `pin`, `demote`, `split`, `merge`, `revert`. All as append-only events with stale-state guards. `IMemoryCurator` + `CurationCapability` separate from ingest/query interfaces. Differentiator vs. Mem0/Letta/Cognee/Zep (which only support point-curation). |

## Open / welcome to refine

- **Test framework**: xUnit chosen, but no strong attachment.
- **Project layout details**: file/folder naming inside `src/Mneme/`
  (e.g., `Storage/`, `Distillation/`, `Projections/`) — not set in
  stone, pick what reads well as you implement.
- **Internal interfaces** (anything not in `Mneme.Contracts`) — free
  to add/refactor as needed.
- **CI**: not set up yet. A GitHub Actions workflow that runs
  `dotnet build` + `dotnet test` on push to main and on PRs would be a
  welcome early PR.

## Working with the planning docs

The `plans/` folder is **planning material lifted from MuxiMuxi**, where
Mneme was incubated. Two things follow from that:

- **Some prose still uses "MuxiMuxi" framing** in places. When you
  notice a section that reads as cockpit-specific rather than
  substrate-general, feel free to rephrase as part of the same PR that
  touches the related code. The intent is for `plans/` to read as
  Mneme-native docs that mention MuxiMuxi only as the first consumer.
- **The 11-phase numbering is the contract.** Phase numbers in
  README.md, `plans/plan.md`, and `plans/backlog.md` must stay in sync.
  If you reorder phases, update all three.

## Commit and PR conventions

**Commit messages**: [Conventional Commits](https://www.conventionalcommits.org/).
Types we use: `feat`, `fix`, `refactor`, `chore`, `docs`, `test`,
`perf`, `build`, `ci`. Scopes match the project name or area:
`contracts`, `mneme`, `mcp`, `plans`, `repo`.

Examples:
- `feat(contracts): add IMemoryAgent.IngestAsync`
- `feat(mneme): port Graphiti extract_nodes prompt`
- `test(contracts): cover CapabilityToken workstream-scoped grants`
- `docs(plans): rebrand plan.md from MuxiMuxi.MemoryAgent to Mneme`

**Commit body** (when non-trivial): explain *why*, not *what*. Cite the
relevant `plans/` section or `research-*.md` finding when porting a
schema or prompt.

**Co-author trailer**: include
`Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`
when an AI agent contributed to the commit. Other agents have their own
conventions — follow your own tool's standard.

**PRs**: keep small. A PR that introduces 5 new interfaces is fine; a
PR that introduces a whole phase is not. Each PR should leave the build
green and tests passing.

## Don't do this

- **Don't add a new package reference to `Mneme.Contracts`.** Anything
  beyond the .NET 8 BCL there is a no-go.
- **Don't push the storage abstraction up into Contracts.** Contracts
  is about API shape; storage is an implementation concern.
- **Don't add MCP types to `Mneme`.** That's `Mneme.Mcp`'s job.
- **Don't change `Directory.Build.props` casually.** It sets NuGet
  metadata that ships with every package. Coordinate via PR + ADR.
- **Don't port a Graphiti prompt without updating `NOTICE`.** Each
  ported prompt needs an entry under "Ported prompts" listing the
  source file path and the destination file path.
- **Don't commit a `.db` file.** Local SQLite files match
  `*.mneme.db*` in `.gitignore`. Use that suffix for any test/scratch DB.
- **Don't skip the test phase to "save time".** If you're shipping a
  public interface, ship a test. The whole point of starting with
  contracts is to make tests possible from day one.

## Where context lives outside this repo

- **MuxiMuxi** (the first consumer):
  <https://github.com/sargeMonkey/muximuxi> — read its
  `product-management/memory-agent/` folder for the
  cockpit-side framing if you need it.
- **Graphiti** (prompt + schema reference):
  <https://github.com/getzep/graphiti> — Apache 2.0; use the file
  paths in `research-zep-sqlite-deepdive.md` to navigate.
- **ModelContextProtocol C# SDK** (Phase 8 dependency, not yet
  referenced): <https://github.com/modelcontextprotocol/csharp-sdk>.

## Questions?

Open a GitHub issue. The repo owner (jacobmsft) is the deciding voice
on anything in the "Locked decisions" table; everything else is fair
game for discussion in a PR.
