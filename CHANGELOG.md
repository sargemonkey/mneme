# Changelog

All notable changes to Mneme are recorded here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
once a stable release is cut. Pre-1.0 the public API may change between
minor versions; breaking changes will be called out in the relevant
release notes.

## [Unreleased]

### Added
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

### Packaging
- Publishable packages: `Mneme.Contracts`, `Mneme`, `Mneme.Agents.AI`
  (libraries), and `Mneme.Mcp` (a `dotnet tool`, command `mneme-mcp`).
- `Mneme.Cli`, `Mneme.Sidecar`, and the `Mneme.Studio*` apps are not packable.
- Package version is centralized in `Directory.Build.props` and overridable
  from the release tag; every package embeds README, LICENSE, NOTICE, XML docs,
  a symbol package, and Source Link.

### Notes
- Pre-1.0 the public API may change between minor versions.
- First planned release: `Mneme.Contracts` / `Mneme` / `Mneme.Agents.AI` /
  `Mneme.Mcp` at `0.1.0-alpha`.
