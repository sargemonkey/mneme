# ADR-0005: Domain profiles via a base Profile SDK + satellite packages

- **Status:** Proposed
- **Date:** 2026-08-09
- **Deciders:** jacobmsft

## Context and Problem Statement

Mneme's seven-category epistemic model (Evidence, Fact, Decision,
Hypothesis, Goal, Action, Outcome) is a good fit for **agent /
engineering** work — decisions, goals, outcomes — but a category
mismatch for other domains. The **writing** domain (fiction,
non-fiction, articles, research papers) wants a different ontology:
characters/concepts, plot/argument **threads**, **claims/canon**,
**setup → payoff** commitments, **continuity/contradiction**, and a
**bi-temporal** split of story-time vs reveal-time.

This is not hypothetical. A real consumer — **MuxiMuxi** (the writer
cockpit) — already runs on Mneme (it persists `thread:<slug>` records
and a `ClaimStore` over Mneme). But a code-grounded audit of its UI
(2026-08-09) showed its continuity capability (`Reconcile`) hands the
Codex "canon" to an LLM as a **flat text digest** with **no structured
(subject, attribute, value) store** underneath, **no reveal-time axis**,
and **no persisted setup→payoff ledger**. Those three gaps are exactly
the *substrate-shaped* ones — the parts that are hard to do app-side and
natural in the memory layer (Mneme already ships deterministic
`(subject, predicate, object)` contradiction detection and a bi-temporal
log).

We want **domain-specialized memory** (ontology + extraction + domain
queries) **without** (a) forking the substrate, (b) bloating base Mneme
with every domain's schema, or (c) reopening the deserialization-safety
property. The question this ADR answers: *how does a satellite package —
`Mneme.Writer`, later `Mneme.Research`, … — contribute a domain profile
(payloads, projections, queries) on top of base `Mneme`?*

## Decision Drivers

- **Preserve every load-bearing invariant.** Append-only log,
  bi-temporal model, rebuildable projections, capability-checked reads,
  inline redaction (#11), the **closed-set deserialization safety**, the
  **no-raw-SQL-escape-hatch** rule (#8), `Mneme.Contracts` BCL-only, and
  the **seven locked categories** must all survive.
- **Base stays domain-agnostic.** Domain code lives in domain packages,
  not in base Mneme.
- **Pay once, then each domain is a package.** A one-time platform cost
  in exchange for cheap marginal domains.
- **Safe extensibility.** The extension set must remain a *curated,
  closed* set assembled from trusted, app-chosen compiled assemblies —
  never a data-driven ontology from untrusted input.

## Considered Options

1. **Code each domain into base** (the `SkillPayload` pattern).
   *Rejected for the many-domain future:* bloats base, couples every
   domain change to a base release, and grows the locked surface.
2. **Pure data-driven ontology from config.** *Rejected:* reopens the
   arbitrary-`$type` deserialization risk, invites the inner-platform
   effect (a worse programming language in YAML), and moves behavior out
   of the type-system / test / review / versioned-contract safety net.
3. **Consumer-library only** — domain logic app-side over generic
   payloads. *Rejected:* this is the "flat-metadata" degradation and is
   exactly MuxiMuxi's current gap — it loses typed payloads, integrated
   contradiction detection, and rebuildable domain projections.
4. **Chosen — base Profile SDK + satellite profile packages.** Base
   exposes a few narrow extension seams; domains ship *compiled* profile
   packages that register into them.

## Decision

Base Mneme gains a **Profile SDK** — a small set of extension seams that
collapse the cross-cutting surface a new payload touches today
(union + redaction + FTS text + prompt/summary + schema + projectors)
into per-payload self-description plus registration:

- **Runtime payload registry.** Replace the static
  `[JsonDerivedType]` union on `EventPayload` with a custom
  `IJsonTypeInfoResolver` assembled *at composition time* from
  explicitly-registered **payload descriptors** — the base "core
  profile" plus whatever satellite profiles the app enabled. Each
  payload is described by `IPayloadDescriptor { Type, Discriminator,
  Category, RedactableFields, ExtractText, Summarize }`, so redaction,
  full-text extraction, and prompt/summary handling become
  self-describing instead of switch statements scattered across the base.
- **Schema modules.** `ISchemaModule { Name; Version; ApplyDdl(conn) }`.
  A profile contributes idempotent DDL + a version; base runs the core
  schema plus registered modules and tracks their versions
  independently. A module may only create/populate its own
  `projection_*` / domain tables.
- **Projector registration.** A profile registers `IProjector`s into the
  pipeline; they participate in projection rebuild-from-log.
- **`IMnemeProfile`** + `MnemeProfileBuilder` +
  `services.AddMneme(…).AddMnemeProfile<T>()`. The eight built-in
  payloads/projectors are refactored into the **core profile**, so the
  base dogfoods its own SDK.

**Satellite packages** (`Mneme.Writer`, later `Mneme.Research`, …) depend
on `Mneme` (+ `Mneme.Contracts`) and implement `IMnemeProfile`. First
profile: **`Mneme.Writer`** — an `AuthoringClaimPayload`
(subject/attribute/value + assertion + `thread_id` + story-time +
reveal-time + grounding-mode + source-ref), thread/commitment/claim
projections, a narrative `ISessionDistiller`, and an `IWriterMemory`
query surface. Its claims **project into the existing
`projection_fact_triples`** so Mneme's deterministic contradiction
detection covers narrative continuity **for free**; **reveal-time** uses
the existing `recorded_at` axis and **story-time** uses `valid_at`.

### Security & invariant preservation (the sensitive part)

- **Closed-set deserialization safety is preserved, not weakened.** The
  payload set stays a *curated, closed* set — only types explicitly
  registered by base + the app's chosen **trusted, compiled** profile
  assemblies resolve; an unknown `$type` is rejected exactly as today.
  Registration is *code in a NuGet dependency the app opted into*, not
  data from an untrusted source. That is the bright line versus option 2.
- **Redaction (#11) fails closed.** Each descriptor must declare its
  free-text/sensitive fields; a payload with no declared handling redacts
  all string fields by default. No payload can silently bypass ingest
  redaction.
- **No raw-SQL escape hatch (#8) on the public API is untouched.** Schema
  modules and projectors are *trusted compiled profile code*, not a
  public caller-facing SQL surface; the public query/curation API gains
  no new SQL entry point.
- **Contracts stays BCL-only.** The abstract `EventPayload` and the
  built-in payloads remain in `Mneme.Contracts`; the resolver, registry,
  schema-module, and projector machinery live in `Mneme`.
- **Seven categories stay locked.** Domain payloads ride under an
  existing category (as `SkillPayload` rides under `Evidence`); the
  domain distinction lives in the payload, not a new category.

## Consequences

**Positive.** Domain-specialized memory without forking the substrate;
base stays domain-agnostic; new domains become packages; MuxiMuxi's three
identified gaps close by *reusing existing engines* (contradiction
detection, bi-temporal log) rather than building bespoke ones; per-payload
self-description removes the recurring "touch every switch" chore for the
built-ins too.

**Costs / risks.** A one-time refactor of *security-sensitive* base code
(payload polymorphism) that must be carefully tested (round-trip +
unknown-`$type`-rejected + redaction-coverage). Loss of the compile-time
exhaustive-switch guarantee, mitigated by descriptors + fail-closed
defaults + tests. Profiles and their schemas need their own versioning
and replay-compatibility discipline (the event log is forever). A generic
resolver is marginally slower than static polymorphism.

**Follow-ups.** Tracked as **Phase 15** in `plans/backlog.md` (A: the
base Profile SDK; B: the `Mneme.Writer` profile). The MuxiMuxi-side swap
onto `Mneme.Writer` is tracked in the `devsanity-ai/muximuxi` repo, not
here. `AuthoringClaimPayload` field names are being aligned to MuxiMuxi's
existing `ClaimStore` / thread record shapes so the package is drop-in.
