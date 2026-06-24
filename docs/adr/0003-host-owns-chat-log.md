# ADR-0003: Host owns the chat log; Mneme owns the interpretation

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** jacobmsft

## Context and Problem Statement

How does conversational signal get into Mneme? The first implementation
shipped a **per-turn capture pipeline** (`ICapturePolicy`,
`CaptureSession`, `ICaptureFilter`, `RecentDuplicateFilter`,
`ConversationTurn`): the host handed Mneme every chat turn, a host-
supplied policy decided whether each turn was worth remembering, and
survivors were ingested as events.

In review this proved to be the wrong shape. The host already owns the
full chat history for every session — append-only, timestamped, always
available. Routing every turn through Mneme duplicated that data, and the
"is this turn worth remembering?" policy tempted hosts to spend an LLM
call **per turn** on the hot path. We needed an ingest model that
reflects the real relationship: the host is the source of truth for raw
conversation; Mneme is the layer that *interprets* it.

## Decision Drivers

- **No data duplication.** Chat logs already live in the host; Mneme
  storing a second copy is waste.
- **Honest provenance.** "Why does memory say X?" must be answerable by
  pointing back at the source turns.
- **Cheap hot path.** Don't force per-turn LLM calls in the request loop.
- **Re-distillation.** Running a better model later should produce new
  memory without re-ingesting raw turns.
- **Bi-temporality preserved.** `valid_at` from the entry timestamp,
  `recorded_at` from when distillation noticed it.

## Considered Options

- **Per-turn capture pipeline** (the original `ICapturePolicy` design).
- **Periodic session distillation with a watermark** (host hands Mneme
  the entries since the last watermark; a host distiller turns them into
  events; Mneme advances the watermark).
- **Stream readers** (Mneme pulls from N host-owned streams via an
  `IStreamReader` abstraction).

## Decision Outcome

Chosen option: **Periodic session distillation with a per-session
watermark.**

The host owns the chat log. Periodically it calls
`IMemoryAgent.DistillSessionAsync(session, entries, capability)` with the
entries that accumulated since Mneme's persisted watermark. Mneme filters
to the un-distilled tail, runs the host-supplied `ISessionDistiller`,
ingests the produced epistemic events stamped with a
`Citation.SessionRange(session, fromEntryId, toEntryId)`, and atomically
advances the watermark. The call is idempotent on
`(session, fromEntryId, toEntryId)`. Mneme stores **no copy of the raw
text** — citations let the host re-resolve source entries on demand.

The per-turn capture surface was **deleted**. The `Mneme.Agents.AI` MAF
provider is **read-only** (no `InvokedAsync` capture pump) so the MAF
hook can't quietly reintroduce per-turn duplication.

### Consequences

- Good: no duplicated chat storage; honest citations; cheap hot path
  (capture is a scheduled batch, not a per-turn LLM call); trivial
  re-distillation; bi-temporality intact.
- Good: directly-ingested events (workflow runs, webhooks, manual
  `remember`) get the same `Citation` treatment via the `Manual` /
  `Workflow` / `External` shapes.
- Bad: eventually-consistent — a fact isn't queryable until the next
  distillation run. Accepted; matches the locked sync-ingest / async-
  distillation split.
- Bad: the host must assign monotonic `EntryId`s and retain its own chat
  log. Accepted — that's the host's existing responsibility anyway.

## Pros and Cons of the Options

### Per-turn capture pipeline (original)

- Good: immediate; simple mental model ("every turn flows through").
- Bad: duplicates the host's chat log; tempts per-turn LLM calls on the
  hot path; no natural watermark / re-distillation story.

### Periodic session distillation + watermark (chosen)

- Good: zero duplication; batched, off-hot-path; idempotent;
  re-distillable; honest provenance.
- Bad: eventual consistency; host must track entry ids.

### Stream readers (`IStreamReader`)

- Good: generalizes to many signal sources.
- Bad: over-engineered for v1; pushes pull-scheduling and back-pressure
  into Mneme; the session-buffer model covers the real cases more simply.

## More Information

- `ARCHITECTURE.md` §1 + §7 — the rule and the session-distillation flow.
- `USAGE.md` §3–4 — the two ingest paths and how to implement
  `ISessionDistiller`.
- `samples/Mneme.Samples.AgentHost` — canonical end-to-end demo.
- `AGENTS.md` → "Locked decisions" → *Host owns the chat log; Mneme owns
  the interpretation*.
- Implemented in the 2026-06 refactor commit
  ("refactor: host owns chat log, Mneme owns interpretation").
