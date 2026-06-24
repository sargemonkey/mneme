# ADR-0002: `IChatClient` instead of Semantic Kernel

- **Status:** Accepted (supersedes the earlier Semantic Kernel plan)
- **Date:** 2026-06-24 (decision predates the repo; recorded retroactively)
- **Deciders:** jacobmsft

## Context and Problem Statement

Mneme makes LLM calls for distillation, classification, and entity-
resolution proposals. Those calls must go through a **pluggable provider
abstraction** so the substrate never hard-codes OpenAI / Anthropic /
Azure and so a host can swap models freely. An earlier iteration of the
plan named Microsoft Semantic Kernel (SK) as that abstraction. This ADR
records the switch to `Microsoft.Extensions.AI.IChatClient`.

## Decision Drivers

- **Minimal dependency surface** on `Mneme.Contracts` (which must stay
  BCL-only) and on `Mneme.Llm` (which should depend on the smallest
  possible LLM abstraction).
- **Industry direction.** The .NET LLM-abstraction story consolidated
  around `Microsoft.Extensions.AI` during 2025–2026.
- **Alignment with Microsoft Agent Framework (MAF).** Mneme ships a MAF
  integration (Phase 8.5); MAF itself dropped Semantic Kernel in favour
  of `Microsoft.Extensions.AI`.
- **Right-sized abstraction.** Mneme needs "send messages, get a
  response," not SK's full plugin/planner/kernel programming model.

## Considered Options

- **`Microsoft.Extensions.AI.IChatClient`** (from
  `Microsoft.Extensions.AI.Abstractions`).
- **Microsoft Semantic Kernel** (`Kernel`, connectors, plugins).
- **A hand-rolled `ILlmProvider` interface** internal to Mneme.

## Decision Outcome

Chosen option: **`Microsoft.Extensions.AI.IChatClient`.**

Mneme's distillation / classification / entity-resolution LLM calls are
expressed against `IChatClient`. The substrate ships **no** LLM SDK
itself; hosts supply an `IChatClient` implementation (OpenAI, Anthropic,
Azure, Ollama, on-device, or a stub for offline tests) wrapped inside
the host-owned `ISessionDistiller` / `IDistiller` / `IEntityProposer`
implementations. See `plans/research-design-lessons.md` §2.15 + §4.7 for
the full rationale, including MAF's own migration off SK.

### Consequences

- Good: tiny, stable dependency; trivially mockable in tests
  (`StubChatClient`); aligns Mneme with MAF and the broader .NET
  ecosystem.
- Good: `Mneme.Contracts` keeps its BCL-only invariant — the
  `IChatClient` dependency lives only where LLM calls are actually made,
  never in the contracts package.
- Bad: we forgo SK's planners/plugins. Accepted — Mneme is a memory
  substrate, not an agent orchestrator; orchestration belongs to the
  host.
- Neutral: tracks a still-evolving abstraction (`Microsoft.Extensions.AI`
  versions move quickly); periodic version bumps are expected.

## Pros and Cons of the Options

### `Microsoft.Extensions.AI.IChatClient`

- Good: minimal, standard, MAF-aligned, easily stubbed, provider-
  agnostic.
- Bad: young and fast-moving; occasional breaking changes between
  preview versions.

### Semantic Kernel

- Good: batteries-included (planners, plugins, connectors, memory
  connectors).
- Bad: heavy dependency for a substrate; **MAF dropped it**; the
  kernel/plugin model is far more than Mneme needs and would leak an
  orchestration framework into a storage library.

### Hand-rolled `ILlmProvider`

- Good: zero external dependency, total control.
- Bad: reinvents a standard abstraction; every host would have to adapt
  its existing `IChatClient` to ours; no ecosystem leverage.

## More Information

- `plans/research-design-lessons.md` §2.15 + §4.7 — MAF dropped SK;
  `IChatClient` is now the .NET LLM abstraction.
- `AGENTS.md` → architectural rule #10 and "Locked decisions" →
  *Pluggable LLM via `IChatClient`*.
- `ARCHITECTURE.md` §9 — the five host-pluggable seams.
