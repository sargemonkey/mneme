# Changelog

All notable changes to Mneme are recorded here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
once a stable release is cut. Pre-1.0 the public API may change between
minor versions; breaking changes will be called out in the relevant
release notes.

## [Unreleased]

### Added
- Repository scaffold: solution + 5 projects (3 shippable + 2 test).
- Apache-2.0 license + `NOTICE` with Graphiti prompt-port attribution.
- `Directory.Build.props` setting net8.0, nullable, warnings-as-errors,
  and NuGet metadata defaults across all packable projects.
- Planning artifacts under `plans/`: build plan, dependency-ordered
  backlog, two research reports, and a consumer architecture
  reference.
- `AGENTS.md` (cross-tool agent onboarding) and
  `.github/copilot-instructions.md`.
- `docs/` placeholder with ADR conventions.

### Not yet
- No implementation code in `src/Mneme.Contracts/`, `src/Mneme/`, or
  `src/Mneme.Mcp/` — Phase 0 contracts is the next PR.
- No CI workflow yet.
- No published NuGet package yet (planned: `Mneme.Contracts` v0.1.0-alpha
  once Phase 0 lands).
