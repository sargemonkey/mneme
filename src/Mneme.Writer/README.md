# Mneme.Writer

The **writing-domain profile** for [Mneme](https://github.com/jacobmsft/mneme) —
a satellite package that teaches the base memory substrate the ontology of
*authored text*: fiction, non-fiction, articles, and research papers.

It is the first concrete **Mneme domain profile** (ADR-0005). It composes onto
base Mneme with one line and adds a first-class **authoring-claim** payload plus
the projector that feeds Mneme's existing structured-contradiction engine — so
**manuscript continuity checking is deterministic**, not a flat digest handed to
an LLM.

```csharp
services.AddMneme(o =>
{
    o.WorkstreamId = "my-novel";
    o.SqlitePath = "novel.mneme.db";
    o.UserId = "author";
})
.AddMnemeWriterProfile();   // ← writing-domain ontology
```

## What it adds

- **`AuthoringClaimPayload`** — a single assertion the manuscript commits to. A
  superset of a flat claim (`Text` / `ScenePath` / `Quote` / `Status` /
  `SourceRef`) **plus** a structured `(Subject, Attribute, Value)` triple and a
  `Grounding` knob (internal canon for fiction vs. external source for
  non-fiction/research). Rides under the locked `Fact` category.
- **`AuthoringCanonProjector`** — projects each claim's triple into the shared
  `projection_fact_triples` index and runs the shared contradiction detector, so
  two scenes that disagree about the same subject + attribute (Helios's eyes are
  *blue* in ch. 2, *green* in ch. 9) surface as a **continuity conflict** for
  review — against base facts and other claims alike, in any ingest order.
- **`IWriterMemory`** — the capability-guarded read surface:
  - `GetContinuityConflictsAsync` — open continuity conflicts in a workstream.
  - `GetOpenCommitmentsAsync` — the manuscript's **unpaid promises / dangling
    setups**: setups with no payoff sharing their commitment id.
- **`NarrativeCommitmentPayload`** — one end of a **setup→payoff** promise. A
  `Setup` (foreshadow / hook / stated intent / claimed contribution) and a later
  `Payoff` share a stable `CommitmentId`; the ledger pairs them so an unpaid
  promise is a first-class, persisted, queryable state — not a per-run LLM
  verdict thrown away. Rides under the `Goal` category (a commitment is an
  outcome the manuscript is pursuing). Owns its projection table
  `projection_narrative_commitments`.

## What it deliberately does *not* add (thin slice)

The continuity half ships no schema of its own: it **reuses** base Mneme's
`projection_fact_triples` and `memory_contradictions` tables. That reuse is the
whole point — structured canon in, free contradiction detection out. Story-time
vs. reveal-time bi-temporality rides on the event envelope's existing `ValidAt` /
`RecordedAt` axes (both are stamped onto commitment ledger rows for a future
"unpaid as of chapter N" query).

Thread-status projection, the narrative distiller, and richer queries
(character dossier, as-of-section) are later phases (see the repo backlog,
Phase 15.B).

## License

Apache-2.0. See `LICENSE` and `NOTICE`.
