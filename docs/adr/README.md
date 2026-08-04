# Architecture Decision Records

This directory holds Mneme's Architecture Decision Records (ADRs) using
the [MADR](https://adr.github.io/madr/) template.

An ADR captures a single architectural decision, the context that forced
it, the options considered, and the consequences. ADRs are immutable once
accepted: to change a decision, write a new ADR that supersedes the old
one (and mark the old one `superseded by ADR-NNNN`).

## When to write an ADR

Per [`AGENTS.md`](../../AGENTS.md), any change to an item in the
**"Architectural rules — do not violate"** or **"Locked decisions"**
tables requires a written ADR *and* an issue discussion first. New
cross-cutting decisions that future contributors might second-guess are
also good ADR candidates.

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-sqlite-only-embedded-backend.md) | SQLite as the only embedded backend | Accepted |
| [0002](0002-ichatclient-over-semantic-kernel.md) | `IChatClient` instead of Semantic Kernel | Accepted |
| [0003](0003-host-owns-chat-log.md) | Host owns the chat log; Mneme owns the interpretation | Accepted |
| [0004](0004-multi-agent-and-dreaming.md) | Multi-agent shared workstreams + offline "dreaming" consolidation | Proposed |

## Numbering

Four-digit, zero-padded, monotonically increasing. The number is
permanent even if the ADR is later superseded or rejected.

## Template

Copy [`0000-template.md`](0000-template.md) for new records.
