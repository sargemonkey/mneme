# ADR-0004: Multi-agent shared workstreams + offline "dreaming" consolidation

- **Status:** Proposed
- **Date:** 2026-07-31
- **Deciders:** jacobmsft

## Context and Problem Statement

Mneme v1 assumes one distiller subscriber per workstream and a
per-session distillation watermark. The next frontier is **multiple
agents collaborating on the same codebase / workstream** (planner +
coder + reviewer, or many parallel workers), plus **offline memory
consolidation** — the "dreaming" idea from Lamis Mukta's *Learning while
you sleep* (AI Native DevCon 2026): an asynchronous, out-of-band pass
that reviews many transcripts across sessions and agents, prunes and
curates, extracts cross-session patterns and reusable skills, and refines
shared memory across workstreams each cycle. Intelligence should
**compound** ("task 50 is not the same as task 1"), which today it does
not.

Mneme is unusually well-placed for the concurrency half: an **append-only
bi-temporal event log with ULID idempotency** is the correct primitive
for N concurrent writers — no lost updates, no last-writer-wins. But four
gaps remain, and three of them touch **locked decisions**, so they need a
written decision before any code lands. This ADR records the shape of
Phase 13 (multi-agent concurrency) and Phase 14 (dreaming) and resolves
the tensions with the locked decisions.

## Decision Drivers

- **Preserve the load-bearing invariants.** Append-only event log,
  bi-temporal model, rebuildable projections, capability-checked reads,
  and "Mneme never stores raw turns" must all survive unchanged.
- **Don't relitigate locked decisions casually.** Conservative entity
  resolution and the seven epistemic categories are locked; any change
  needs explicit justification here.
- **Compounding intelligence.** The point of dreaming is that memory
  gets *better* over time (dedup, abstraction, skill extraction), not
  just bigger.
- **Isolation by default.** Multiple agents sharing a workstream must not
  contaminate each other's context; promotion of a memory to "shared"
  should be deliberate, not incidental.
- **Backwards compatibility.** Single-agent, single-session hosts must
  keep working with zero changes; every new surface is additive and
  nullable-optional.

## Considered Options

1. **Do nothing** — keep one-subscriber-per-workstream; tell multi-agent
   hosts to shard into one workstream per agent.
2. **Bolt locking / CRDTs onto a mutable store** — the path Mem0 / Letta
   / Cognee take; add concurrency control to in-place mutation.
3. **Extend the existing append-only substrate** with (a) an agent/role
   scope axis, (b) cross-agent contradiction handling via the existing
   review queue, (c) a projection-level visibility tier, and (d) an
   offline `IDreamer` + `DreamJob` consolidation loop that operates over
   the event log — never raw transcripts.

## Decision Outcome

Chosen option: **Option 3 — extend the append-only substrate.**

Phase 13 (concurrency) and Phase 14 (dreaming) land as additive,
nullable-optional surfaces. Nothing about the event log, bi-temporal
model, or capability gate changes; the new work is scope filtering,
projection metadata, and a new offline worker.

### Phase 13 — multi-agent concurrency on a shared workstream

- **Agent/role scope axis** (`phase13-agent-role-scope`). `PrincipalId`
  already models an agent; formalize it on the read path. Add an optional
  agent/role filter to `QuerySpec` and an optional agent scope to
  `CapabilityToken` (both nullable → non-breaking). Enables role-scoped
  views ("what did the reviewer learn") without a new isolation boundary.
- **Cross-agent contradiction detection** (`phase13-contradiction-detection`).
  Bi-temporal supersession assumes *sequential* observation. Two agents
  concurrently asserting `F` and `¬F` is a **conflict, not a
  supersession**. On conflict, raise a `Hypothesis` (state `open`) or an
  `IReviewQueue` item instead of silently resolving — reusing the
  existing `IReviewQueue` + `WorkstreamMode.ReviewBeforeDistill`
  machinery, not inventing new types.
- **Visibility tier** (`phase13-visibility-tier`). Add a visibility
  dimension (`private-to-session` → `shared-in-workstream` → `global`) on
  the **projection layer**, never on the event log (the log stays whole
  and rebuildable). Session-scoped events are *candidate*; the dreaming
  loop promotes vetted ones to workstream-visible. This is the
  context-contamination fix.

### Phase 14 — offline "dreaming" / consolidation

- **`Citation.Derived` provenance** (`phase14-citation-derived`). Add
  `Citation.Derived(IReadOnlyList<EventId> From, string ConsolidatorId)`
  as a new `[JsonDerivedType]` on the closed `Citation` set. This lets a
  consolidated event name the events it was distilled from, keeping the
  audit chain intact and projections rebuildable — and, crucially, it
  lets dreaming operate over Mneme's **own epistemic event log**, not raw
  transcripts (see Tension 3).
- **`IDreamer` + `DreamJob`** (`phase14-idreamer-dreamjob`). A new
  host-supplied `IDreamer` (symmetric to `ISessionDistiller` /
  `IDistiller`, with a versioned `Id`) operating over an **event-range
  query across sessions/agents**, not a single session. A scheduled
  `DreamJob` worker (idle-triggered or cron) runs the loop **replay →
  abstract → reconcile → promote** — mapping to the "light / REM / deep
  sleep" phasing in the literature. It reads projections and emits new
  events stamped `Citation.Derived`.
- **`Skill` procedural-memory artifact** (`phase14-skill-category`). The
  seven categories cover *what is true*; none cover *how we reliably do
  X* — the "extract general skills" the talk emphasizes. Modelled as a
  distinct **procedural-memory projection** (preferred) or an eighth
  `Skill` category. See Tension 2.
- **Cross-session reconciliation** (`phase14-cross-session-reconcile`).
  The dreaming loop dedups and prunes across concurrent sessions and
  **proposes** entity merges into the review queue (never auto-applies —
  see Tension 1).
- **Cross-workstream global skills** (`phase14-fleet-global-skills`). Run
  the dreamer with a cross-workstream capability to mine patterns *across*
  workstreams into a `global`-visibility skill library — an instance-wide
  refinement, building on `PinScope.Global`. Because this is the one job
  that reads across the isolation boundary, it carries the strongest
  privacy guardrails (see "Privacy & compliance" below).

### Consequences

- Good: the append-only log becomes the concurrency primitive for a
  swarm *for free*; no locking, no CRDTs, no mutable-store race handling.
- Good: every new surface is additive/nullable — single-agent hosts are
  untouched.
- Good: dreaming makes memory *compound* (dedup, abstraction, skills)
  while every derived memory stays fully auditable via `Citation.Derived`.
- Bad / accepted: two new host seams (`IDreamer` + the visibility/scope
  plumbing) enlarge the integration surface. Mitigated by keeping them
  optional — a host that wires neither gets exactly today's behaviour.
- Bad / accepted: the `DreamJob` is a new always-considered background
  worker with its own scheduling, back-pressure, and cost profile
  (LLM-heavy). Mitigated by making it opt-in and idle-triggered.
- Neutral: phase numbering in `README.md`, `plans/plan.md`, and
  `plans/backlog.md` must be updated in lockstep when these land.

## Tensions with locked decisions (and their resolutions)

These are the reason this ADR exists. Each locked decision is preserved,
not overturned.

### Tension 1 — Conservative entity resolution

*Locked:* auto-merge only on deterministic keys; LLM-judgment merges go
through propose-then-confirm and never auto-apply
(`AGENTS.md` rule #9). *Dreaming wants to* merge duplicate entities it
discovers across sessions. **Resolution:** the dreamer **proposes** merges
into the existing review/confirm pipeline; it never auto-applies an
LLM-judged merge. Tier-1 deterministic-key merges continue to auto-apply
as today. The invariant is preserved verbatim — dreaming is just another
*proposer*.

### Tension 2 — Seven epistemic categories

*Locked (effectively):* the seven categories are a load-bearing part of
the data model and appear across README / plan / backlog. *Dreaming
introduces* procedural "skills" (how-to knowledge), which none of the
seven capture. **Resolution:** prefer a **distinct procedural-memory
projection** (`Skill` as its own table + contract type) over stretching
`Decision`, so the seven epistemic categories stay exactly seven. If a
future review prefers an eighth enum member instead, that is a follow-up
ADR; either way the numbering docs are updated in lockstep. This ADR
authorizes the *concept*; the concrete surface is decided in
`phase14-skill-category`.

### Tension 3 — "Mneme never stores raw turns"

*Locked:* the host owns the chat log; Mneme stores only the distilled
interpretation plus `Citation.SessionRange` pointers (ADR-0003). *Naïve
dreaming* (as some products do it) re-reads raw transcripts. **Resolution:**
dreaming consumes Mneme's **own event log and projections**, never raw
transcripts. Consolidated events cite their sources with the new
`Citation.Derived` shape; if deeper detail is genuinely needed, the
dreamer re-resolves an underlying `Citation.SessionRange` back through the
host on demand — exactly the ADR-0003 mechanism. The invariant is
preserved: Mneme still stores no raw turns.

## Privacy & compliance in the multi-user shift

The v1 privacy primitives were designed for **one operator plus their own
agents**. When principals become **distinct people**, three existing
primitives change meaning and the dreaming loop opens a new
aggregation path. These constraints are **binding** on the Phase 13/14
implementation — not aspirational.

### What already holds (and stays)

- Capability-gated reads with no raw-SQL escape hatch (`IMemoryQueryAPI`).
- Workstream isolation by default; cross-workstream requires an explicit
  grant (`CapabilityToken.CrossWorkstream` + `Workstream == null`).
- Inline secret redaction in the **sync** ingest stage, before the WAL
  (`IRedactor` / `RegexRedactor`).
- Classification labels `Public / Internal / Confidential / Secret / Pii`.
- Append-only + revocable content (tombstone zeroes the artifact body,
  keeps metadata) — satisfies "retain forever AND legally delete."
- Curation as a **separate authority** (`CurationCapability` ≠
  `CapabilityToken`), and a full processing audit trail (`ICurationLog`,
  GDPR Art. 30).

### What newly binds when principals are people

1. **Indexed subject access & erasure.** `PrincipalId` is opaque and
   author identity currently lives inside `provenance_json` (a blob).
   Data-subject *access* ("everything about principal X") and *erasure*
   ("delete everything principal X authored") must be **O(index)**, not a
   full-table provenance scan. **Binding:** promote `principal_id` (and
   `agent_id` where distinct) to an indexed column on `memory_events` and
   the projections. This is the *same* column `phase13-agent-role-scope`
   needs — privacy and the scope feature share one change.

2. **Sensitive classes are private by default.** A shared workstream is a
   data-sharing surface between people; a `Pii` / `Confidential` label is
   a read gate, not a containment boundary. **Binding:** events
   classified `Pii` / `Confidential` / `Secret` default to the
   `private-to-session` visibility tier (author-only) and require an
   explicit curation step to promote to `shared`. This makes the
   `phase13-visibility-tier` mechanism double as the PII-containment
   boundary — contamination-prevention and privacy are one mechanism.

3. **Cross-workstream grants are cross-*person* grants.** The same flag
   that once meant "query across my own workstreams" now means "read
   across other people's workstreams." **Binding:** cross-workstream
   tokens are privileged — short-lived (enforced via `NotBefore` /
   `NotAfter`), logged as an access event, and **never** issued as a
   standing grant to an automated consolidator without an explicit
   per-workstream opt-in (see #4 below).

### Guardrails specific to the dreaming loop

The consolidator is, by construction, the highest-privilege actor: it can
read across the isolation boundary and write a shared artifact. Five
guardrails, all enforced in code (not by convention):

1. **Classification floor on promotion.** The dreamer may promote to
   `global` visibility **only** from source events classified `Public` or
   `Internal`. `Confidential` / `Secret` / `Pii` sources are *ineligible*
   for global promotion — enforced in the promoter.
2. **Re-run the redactor on derived output.** Secret redaction runs at
   ingest today; run it **again on every dreamer-produced event** before
   write, because an LLM can synthesize a secret-shaped string from
   fragments across sources.
3. **Capability-gate `Citation.Derived` traversal.** Resolving a derived
   event's `From` list re-checks the caller's capability against *each*
   source event's workstream, so following a citation cannot cross a
   boundary the caller could not cross directly.
4. **Opt-in participation.** A `workstream_config` flag
   `ParticipatesInCrossWorkstreamConsolidation` (default **false**). A
   workstream is never mined for the global library unless it has
   explicitly opted in.
5. **Full audit of consolidator reads.** The `DreamJob` records what it
   read and what it produced. As the highest-privilege actor, it is the
   most-logged.

### Consequences for the roadmap

- `phase13-agent-role-scope` also delivers indexed subject access /
  erasure (promote `principal_id` to a column).
- `phase13-visibility-tier` also delivers PII containment (sensitive
  classes private-by-default).
- The five dreamer guardrails are **acceptance criteria** for
  `phase14-idreamer-dreamjob` and `phase14-fleet-global-skills`, not
  optional polish.

## Pros and Cons of the Options

### Option 1 — Do nothing / one workstream per agent

- Good: zero new code; isolation is trivially total.
- Bad: defeats the point — collaborating agents on one codebase can't
  share a memory; no cross-agent synthesis; no compounding; sharding
  explodes workstream count and breaks "what does the team know about X".

### Option 2 — Locking / CRDTs on a mutable store

- Good: familiar to hosts coming from Mem0 / Letta.
- Bad: throws away Mneme's core advantage. An append-only bi-temporal log
  already gives conflict-free concurrent writes; bolting locking onto a
  mutable store reintroduces exactly the race/merge problems the log
  design avoids, and breaks the "rebuildable from the log" invariant.

### Option 3 — Extend the append-only substrate (chosen)

- Good: preserves every locked invariant; additive and nullable;
  concurrency falls out of the existing log; dreaming stays fully
  auditable.
- Bad: two new host seams and a background worker to design, schedule,
  and cost-manage. Accepted as opt-in.

## More Information

- Lamis Mukta, *Learning while you sleep: Beyond memory to dreaming* — AI
  Native DevCon 2026 (motivating talk).
- ADR-0003 — *Host owns the chat log; Mneme owns the interpretation*
  (the invariant Tension 3 preserves).
- Privacy primitives referenced above: `CapabilityToken`,
  `CurationCapability`, `Classification` (`Enums.cs`), `IRedactor` /
  `RegexRedactor`, `IRevocationService`, `ICurationLog` (GDPR Art. 30).
- `AGENTS.md` → "Architectural rules" #4, #5, #6, #9 and "Locked
  decisions" (conservative entity resolution; seven categories;
  bi-temporal; append-only).
- Backlog: `adr-multiagent-dreaming` (this ADR) → Phase 13
  (`phase13-agent-role-scope`, `phase13-contradiction-detection`,
  `phase13-visibility-tier`) → Phase 14 (`phase14-citation-derived`,
  `phase14-skill-category`, `phase14-idreamer-dreamjob`,
  `phase14-cross-session-reconcile`, `phase14-fleet-global-skills`).
