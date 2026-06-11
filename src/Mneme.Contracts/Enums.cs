namespace Mneme.Contracts;

/// <summary>
/// The seven epistemic categories Mneme uses to classify what kind of
/// claim an event represents. See <c>plans/plan.md</c> §"Seven epistemic
/// categories" for the rationale.
/// </summary>
public enum EpistemicCategory
{
    /// <summary>Raw observation — chat message, document excerpt, signal payload.</summary>
    Evidence = 0,

    /// <summary>Synthesized atomic claim derived from one or more evidence items.</summary>
    Fact = 1,

    /// <summary>A choice made by a human or agent, with a rationale and supporting evidence.</summary>
    Decision = 2,

    /// <summary>A claim under investigation, with state <c>open | confirmed | refuted | abandoned</c>.</summary>
    Hypothesis = 3,

    /// <summary>An outcome a workstream is pursuing, with state <c>active | achieved | abandoned</c>.</summary>
    Goal = 4,

    /// <summary>An executed step that affects the outside world (PR opened, email sent, ticket created).</summary>
    Action = 5,

    /// <summary>An observation about what happened after an <see cref="Action"/> — the closure of a decision loop.</summary>
    Outcome = 6,
}

/// <summary>
/// Distinguishes epistemic events (the things Mneme is *for*) from technical
/// events (workflow checkpoints, internal bookkeeping). Queries default to
/// <see cref="Epistemic"/> only; callers must set
/// <see cref="CapabilityToken.IncludeTechnical"/> to surface technical events.
/// </summary>
public enum EventChannel
{
    /// <summary>Belongs to the seven epistemic categories — first-class memory.</summary>
    Epistemic = 0,

    /// <summary>Technical bookkeeping (e.g., MAF workflow checkpoints). Filtered out by default.</summary>
    Technical = 1,
}

/// <summary>
/// Data classification labels attached at ingest time. Drive retention,
/// revocation, and capability-based read gates.
/// </summary>
public enum Classification
{
    /// <summary>Default; no special handling.</summary>
    Public = 0,

    /// <summary>Internal to the workstream owner; not auto-shared.</summary>
    Internal = 1,

    /// <summary>Sensitive; capability token must explicitly grant confidential read.</summary>
    Confidential = 2,

    /// <summary>Maximally sensitive; subject to retention limits and access logging.</summary>
    Secret = 3,

    /// <summary>Personally identifiable information; redactor should have removed it before ingest.</summary>
    Pii = 4,
}

/// <summary>
/// The kinds of human-in-the-loop curation actions Mneme supports. Each maps
/// to an append-only event type (e.g., <see cref="Amended"/> → <c>fact.amended</c>).
/// See <c>plans/plan.md</c> §"Human-in-the-loop curation".
/// </summary>
public enum CurationType
{
    /// <summary>Content correction; old fact superseded, queryable bi-temporally.</summary>
    Amended = 0,

    /// <summary>Human commentary attached to a target event; non-destructive.</summary>
    Annotated = 1,

    /// <summary>Retrieval-weight boost (default multiplier 2.0).</summary>
    Pinned = 2,

    /// <summary>Retrieval-weight suppression (default multiplier 0.3).</summary>
    Demoted = 3,

    /// <summary>Aggregated fact declared as N separate facts.</summary>
    Split = 4,

    /// <summary>N facts declared as one.</summary>
    Merged = 5,

    /// <summary>Inverse of a prior curation; restores prior state.</summary>
    Reverted = 6,
}

/// <summary>
/// Per-workstream policy for whether captured events go straight to
/// distillation or wait for human review.
/// </summary>
public enum WorkstreamMode
{
    /// <summary>Default; distillation worker processes new events automatically.</summary>
    AutoDistill = 0,

    /// <summary>Distillation worker skips events until <see cref="IReviewQueue.ApproveAsync"/> is called.</summary>
    ReviewBeforeDistill = 1,
}

/// <summary>
/// Scope of a <see cref="CurationType.Pinned"/> or <see cref="CurationType.Demoted"/> curation.
/// </summary>
public enum PinScope
{
    /// <summary>Multiplier applies only within the source workstream.</summary>
    Workstream = 0,

    /// <summary>Multiplier applies whenever this event is retrieved, regardless of querying workstream.</summary>
    Global = 1,
}
