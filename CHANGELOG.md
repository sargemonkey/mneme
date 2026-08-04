# Changelog

All notable changes to Mneme are recorded here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
once a stable release is cut. Pre-1.0 the public API may change between
minor versions; breaking changes will be called out in the relevant
release notes.

## [Unreleased]

### Security
- **Fixed: ingest secret redaction was a silent no-op in DI-wired hosts.**
  Registering the redactor as `TryAddSingleton<IRedactor, RegexRedactor>()` let
  the DI container pick `RegexRedactor`'s greedy `IEnumerable<RedactionRule>`
  constructor and resolve it to an **empty** rule set — so the inline secret
  redactor (locked decision #11) stripped nothing at ingest. Now registered via a
  factory (`_ => new RegexRedactor()`) so the default rule set is used. Affected
  `AddMneme`, `Mneme.Studio`, and `Mneme.Studio.Desktop`; verified with an
  end-to-end ingest-redaction regression test. Direct `new RegexRedactor()` usage
  (benchmarks, unit tests) was always correct, which is why this went unnoticed.

### Added
- **Cross-agent contradiction detection (Phase 13, ADR-0004):** two currently-
  valid structured triples with the same `subject_key` + `predicate` but a
  different `object` are recorded as open candidates in a new
  `memory_contradictions` projection by a `ContradictionsProjector`, instead of a
  silent bi-temporal supersession (which assumes sequential observation). Narrow
  and deterministic (structured triples only, trim/case-insensitive object
  compare); candidates surface for human review and are never auto-resolved.
  Schema v14→v15. Tested (`ContradictionDetectionTests`).
- **Procedural memory / skills (Phase 14, ADR-0004):** a new `SkillPayload`
  ("how we reliably do X") with its own `projection_skills` read model and
  `SkillsProjector`. Skills ride in the append-only log under the `Evidence`
  category (so the seven epistemic categories stay locked) and the projector
  recognises them by payload type; typically ingested with a `Citation.Derived`
  provenance. Schema v13→v14. Tested (`SkillProjectionTests`).
- **Agent/role scope + data-subject primitive (Phase 13, ADR-0004):** an indexed
  `memory_events.principal_id` column (mirrors provenance author identity) plus a
  nullable `QuerySpec.Principal` filter. Scopes reads to a single agent/user
  within a shared workstream and makes "everything principal X authored" an
  O(index) query — the basis for data-subject access and erasure. Enforced across
  all read paths (structured, free-text, list-recent). Schema v11→v12.
- **Read-side visibility tier (Phase 13, ADR-0004):** a `Visibility`
  (`Private`/`Shared`/`Global`) dimension in a mutable `memory_visibility` sidecar
  (keyed by event id, so promotion never mutates the append-only log). Sensitive
  classes (`Pii`/`Confidential`/`Secret`) default to `Private` (author-only);
  everything else to `Shared`. `Private` events are readable only by their
  authoring principal — the PII-containment boundary in a shared workstream.
  Enforced across all read paths. Schema v12→v13.
- **`Citation.Derived(From[], ConsolidatorId)`** — a new provenance shape on
  the polymorphic `Citation` set for events derived from *other Mneme events*
  (the offline consolidation / "dreaming" pass), as opposed to a source signal.
  Keeps the audit chain intact and projections rebuildable, and lets
  consolidation operate over Mneme's own event log rather than raw transcripts
  (preserves the host-owns-the-chat-log invariant). First increment of Phase 14.
- **ADR-0004** (`docs/adr/0004-multi-agent-and-dreaming.md`, Proposed) —
  records the design for Phase 13 (multi-agent shared workstreams) and Phase 14
  (offline dreaming/consolidation), resolves the three locked-decision tensions
  (conservative entity resolution, seven epistemic categories, "never store raw
  turns"), and adds a **binding privacy & compliance section** for the
  single-user → multi-user shift (indexed subject access/erasure, PII-private-
  by-default visibility, and five consolidator guardrails).
- **`Mneme.Studio.Agent`** reference consumer (not shipped as a package): a
  Photino + Blazor desktop app that is an ACP client (`LibAcp`) driving GitHub
  Copilot over `copilot --acp`, using Mneme's own distillers (LLM = Copilot) to
  distill a turn-based conversation into epistemic memory. Includes LoCoMo-shaped
  corpus replay, per-memory reject (revocation), and a sleep/consolidate view.

- **Phases 0–12 implemented** across `Mneme.Contracts`, `Mneme`,
  `Mneme.Agents.AI`, and `Mneme.Mcp`: append-only bi-temporal SQLite event
  log, classification + revocation, projections + FTS5, capability-checked
  query API (Explain + AsOf), LLM distillation (`IDistiller` +
  `ISessionDistiller`), three-tier entity resolution, outcome closure, HITL
  curation, MCP server, MAF `MnemeContextProvider`, HTTP sidecar, and cloud
  snapshot sync.
- Semantic retrieval (`VectorIndex`, brute-force cosine) + hybrid
  semantic/BM25/recency query fusion; pluggable `IReranker` (6th host seam).
- **Subject-attributed knowledge graph (Phase 12):** `FactTriple` +
  `projection_fact_triples` + `FactTriplesProjector` + subject-scoped query
  boost + append-only `QueryResult.SubjectTriples` answer-context supplement +
  `SubjectTripleResolver`.
- LoCoMo benchmark harness with Mem0-aligned methodology; published results in
  `benchmarks/Mneme.Benchmarks.LoCoMo/RESULTS.md` (**89.6% Mem0-comparable at
  parity; 80.3% at ~9× less context**).
- CI workflow (`ci.yml`, build + test, warnings-as-errors) and a tag-driven
  **release workflow** (`release.yml`) that packs + pushes to nuget.org.
- ADR index (`docs/adr/`) and publishing runbook (`docs/PUBLISHING.md`).

### Performance
- Query hot paths batch candidate/subject-key lookups into single queries
  (in-memory gating unchanged) and the fact-triple projector reuses a prepared
  insert command — fewer allocations, better scaling. See
  `docs/pre-release-review-2026-07.md`.

### Documentation
- README rewritten as a product-facing readme (install, quickstart, packages,
  benchmark headline) with the phase-status board removed.
- New `docs/INTEGRATION.md` one-shot integration recipe (agent-friendly) and
  `docs/pre-release-review-2026-07.md` (perf + security review record).

### Security
- Sidecar bearer-token auth now uses a constant-time comparison
  (`CryptographicOperations.FixedTimeEquals`) instead of `string.Equals`,
  closing a timing side-channel on the auth path (pre-release review).
- Override the transitive `SQLitePCLRaw` stack (pulled by `Microsoft.Data.Sqlite`)
  to `bundle_e_sqlite3` **3.0.3**, which ships patched SQLite **3.50.4** and is
  out of the vulnerable range of **CVE-2025-6965** / GHSA-2m69-gcr7-jv3q
  (`SQLitePCLRaw.lib.e_sqlite3 <= 2.1.11`, high severity). Clears the NU1903
  audit warning for downstream consumers and fixes the underlying native SQLite
  memory-corruption risk while staying on the net8-friendly 8.x line of
  `Microsoft.Data.Sqlite`.

### Packaging
- Publishable packages: `Mneme` (library) and `Mneme.Agents.AI` (library), plus
  `Mneme.Mcp` (a `dotnet tool`, command `mneme-mcp`).
- `Mneme.Contracts` stays a separate csproj (BCL-only allowlist boundary) but its
  assembly is **folded into the `Mneme` package** — customers install one
  package for both contracts and implementation.
- `Mneme.Cli`, `Mneme.Sidecar`, and the `Mneme.Studio*` apps are not packable.
- Package version is centralized in `Directory.Build.props` and overridable
  from the release tag; every package embeds README, LICENSE, NOTICE, XML docs,
  a symbol package, and Source Link.

### Notes
- Pre-1.0 the public API may change between minor versions.
- First planned release: `Mneme` / `Mneme.Agents.AI` / `Mneme.Mcp` at
  `0.1.0-alpha`.
