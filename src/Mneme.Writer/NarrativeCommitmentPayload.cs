using Mneme.Contracts;

namespace Mneme.Writer;

/// <summary>
/// Which half of a setup→payoff pair a <see cref="NarrativeCommitmentPayload"/>
/// records. A <em>commitment</em> is a promise the document makes and must keep
/// (a foreshadow, a hook, a stated intent, a claimed contribution); the
/// <see cref="Setup"/> establishes it and a later <see cref="Payoff"/> with the
/// same <see cref="NarrativeCommitmentPayload.CommitmentId"/> discharges it.
/// </summary>
public enum CommitmentRole
{
    /// <summary>Establishes a promise the text must later keep (Chekhov's gun on the mantel).</summary>
    Setup = 0,

    /// <summary>Delivers on / resolves a prior <see cref="Setup"/> sharing the same commitment id (the gun is fired).</summary>
    Payoff = 1,
}

/// <summary>
/// One end of a narrative <em>setup→payoff</em> commitment — the writing
/// domain's first-class model of "a promise the document must keep" (ADR-0005 /
/// Phase 15.B). A <see cref="CommitmentRole.Setup"/> and a matching
/// <see cref="CommitmentRole.Payoff"/> share a stable
/// <see cref="CommitmentId"/>; the <see cref="NarrativeCommitmentProjector"/>
/// pairs them into a ledger so an <b>unpaid promise / dangling setup</b> — a
/// setup with no payoff — is a first-class, queryable, persisted state rather
/// than a per-run LLM verdict thrown away after a "trace" pass.
/// </summary>
/// <remarks>
/// <para>
/// Rides under <see cref="EpistemicCategory.Goal"/>: a commitment is literally
/// an outcome the manuscript is pursuing — open while the payoff is outstanding,
/// achieved once it lands — so it lives in the append-only log under one of the
/// seven locked categories while the profile's projector recognises it by
/// payload type (the same "ride under an existing category" pattern
/// <see cref="SkillPayload"/> uses under Evidence).
/// </para>
/// <para>
/// The two bi-temporal axes ride on the event envelope, not this payload:
/// <c>ValidAt</c> is <em>story-time</em> (when the setup/payoff happens in the
/// narrated world) and <c>RecordedAt</c> is <em>reveal-time</em> (when the
/// draft/reader gets it). The caller sets both on the <see cref="CaptureEvent"/>.
/// </para>
/// <para>
/// A commitment is discharged by <em>appending</em> a payoff event (never by
/// mutating the setup) — append-only, and re-distillation just adds rows.
/// </para>
/// </remarks>
/// <param name="CommitmentId">Stable slug linking a setup to its payoff(s), e.g. <c>commitment:chekhovs-gun</c>. Reused verbatim on both ends.</param>
/// <param name="Role">Whether this event establishes the promise (<see cref="CommitmentRole.Setup"/>) or delivers it (<see cref="CommitmentRole.Payoff"/>).</param>
/// <param name="Description">Human-readable statement of the promise or its delivery (e.g. "a loaded gun hangs over the mantel").</param>
/// <param name="Kind">Sub-domain flavour of the promise — <c>foreshadow</c> (fiction), <c>promise</c> (non-fiction intro), <c>hook</c> (article), <c>contribution</c> (research). Open vocabulary; defaults to <c>promise</c>.</param>
/// <param name="ScenePath">Manuscript passage that establishes/delivers it, if any.</param>
/// <param name="Quote">Verbatim quote anchoring it to that passage, if any.</param>
public sealed record NarrativeCommitmentPayload(
    string CommitmentId,
    CommitmentRole Role,
    string Description,
    string Kind = "promise",
    string? ScenePath = null,
    string? Quote = null)
    : EventPayload
{
    /// <inheritdoc/>
    public override EpistemicCategory Category => EpistemicCategory.Goal;
}
