namespace Mneme.Contracts;

/// <summary>
/// Host-supplied offline consolidation ("dreaming") engine — the third LLM seam
/// alongside <see cref="ISessionDistiller"/> (turns → events) and
/// <see cref="IDistiller"/> (events → bundle). A dreamer reviews a slice of a
/// workstream's already-distilled memory and produces <em>derived</em> events:
/// extracted skills, reconciliations, cross-session syntheses. The SDK
/// orchestrates the call — loads the events, stamps each output with a
/// <see cref="Citation.Derived"/> provenance, applies the safety guardrails, and
/// ingests through the normal validate → redact → classify → WAL pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Authority model (ADR-0004): dreamer outputs are <strong>direct-ingested</strong>
/// as append-only events, but gated by guardrails the SDK enforces — the output
/// is re-run through the ingest redactor, and its requested visibility is capped
/// by the sensitivity of the events it was derived from. Entity merges and
/// contradiction <em>resolutions</em> are proposals only; a dreamer never mutates
/// effective state in place (there is no update path — everything is a new event).
/// </para>
/// <para>
/// Symmetric to the other seams: hosts wire an <c>IChatClient</c>-backed
/// implementation, a heuristic, or none. When no dreamer is registered the
/// consolidation worker is simply inert.
/// </para>
/// </remarks>
public interface IDreamer
{
    /// <summary>
    /// Stable identifier (model + prompt revision), stamped as the
    /// <see cref="Citation.Derived.ConsolidatorId"/> on every event this dreamer
    /// produces, so consumers can tell which consolidator authored a memory.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Review the request's events and return the consolidated outputs worth
    /// keeping. Returning an empty result is fine (and common for a workstream
    /// with nothing new to abstract).
    /// </summary>
    Task<DreamResult> DreamAsync(DreamRequest request, CancellationToken ct = default);
}

/// <summary>
/// Everything an <see cref="IDreamer"/> needs to consolidate a slice of memory.
/// The SDK builds this; the dreamer consumes it.
/// </summary>
/// <param name="Workstream">Workstream being consolidated.</param>
/// <param name="GeneratedAt">When the SDK began assembling this request.</param>
/// <param name="Events">Capability-allowed, non-revoked epistemic events in scope, newest first. Reuses the pre-decoded shape the read-side distiller consumes.</param>
/// <param name="PriorSkills">Skills already in the workstream, surfaced so the dreamer avoids re-deriving them.</param>
/// <param name="OpenContradictions">Unresolved contradiction candidates the dreamer may propose reconciliations for.</param>
/// <param name="TokenBudget">Soft total token budget for the request.</param>
public sealed record DreamRequest(
    WorkstreamId Workstream,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DistillationEvent> Events,
    IReadOnlyList<PriorSkill> PriorSkills,
    IReadOnlyList<ContradictionCandidate> OpenContradictions,
    int TokenBudget);

/// <summary>A skill already known to the workstream, surfaced to the dreamer as background.</summary>
/// <param name="EventId">The skill event's id.</param>
/// <param name="Name">Short imperative title.</param>
/// <param name="Procedure">The how-to.</param>
/// <param name="Trigger">Optional applicability context.</param>
public sealed record PriorSkill(
    EventId EventId,
    string Name,
    string Procedure,
    string? Trigger);

/// <summary>
/// An unresolved contradiction candidate (two currently-valid triples with the
/// same subject + predicate but a different object), surfaced so the dreamer can
/// propose a reconciliation.
/// </summary>
/// <param name="SubjectKey">Normalized subject key both triples share.</param>
/// <param name="Predicate">Predicate both triples share.</param>
/// <param name="EventIdA">The earlier-sorted source event.</param>
/// <param name="ObjectA">Its asserted object.</param>
/// <param name="EventIdB">The later-sorted source event.</param>
/// <param name="ObjectB">Its asserted object.</param>
public sealed record ContradictionCandidate(
    string SubjectKey,
    string Predicate,
    EventId EventIdA,
    string ObjectA,
    EventId EventIdB,
    string ObjectB);

/// <summary>What the dreamer returns.</summary>
/// <param name="Outputs">The consolidated events to ingest, in arbitrary order.</param>
public sealed record DreamResult(
    IReadOnlyList<DreamOutput> Outputs);

/// <summary>
/// One event the dreamer decided to produce. The SDK stamps it with a
/// <see cref="Citation.Derived"/> covering <paramref name="DerivedFrom"/> and
/// ingests it; <paramref name="ProposedVisibility"/> is a request the SDK caps
/// by the sensitivity of the derived-from events (never trusted blindly).
/// </summary>
/// <param name="Payload">The typed epistemic payload (typically a <see cref="SkillPayload"/>).</param>
/// <param name="DerivedFrom">The events this output was consolidated from. Must be non-empty.</param>
/// <param name="ProposedVisibility">The visibility the dreamer wants; the SDK clamps it to what the sources allow.</param>
public sealed record DreamOutput(
    EventPayload Payload,
    IReadOnlyList<EventId> DerivedFrom,
    Visibility ProposedVisibility = Visibility.Shared);
