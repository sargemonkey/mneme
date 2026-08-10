using Mneme.Contracts;

namespace Mneme.Writer;

/// <summary>
/// How an <see cref="AuthoringClaimPayload"/>'s truth is judged — the single
/// orthogonal knob that separates the writing sub-domains while keeping one
/// shared ontology (ADR-0005 / Phase 15).
/// </summary>
public enum AuthoringGrounding
{
    /// <summary>
    /// Truth is whatever the manuscript itself established. Continuity is
    /// <em>internal consistency</em> — the fiction "canon" case (a character's
    /// eye colour is whatever the text last committed to).
    /// </summary>
    InternalCanon = 0,

    /// <summary>
    /// Truth is an external, citable source. The claim must match reality — the
    /// non-fiction / article / research-paper case (a stated result must match
    /// the cited work).
    /// </summary>
    ExternalSource = 1,
}

/// <summary>
/// A single assertion the manuscript commits to — the writing-domain refinement
/// of a <see cref="FactPayload"/>. It is a superset of MuxiMuxi's flat
/// <c>Claim</c> record (<see cref="Text"/> / <see cref="ScenePath"/> /
/// <see cref="Quote"/> / <see cref="Status"/> / <see cref="SourceRef"/>, so it
/// is a drop-in for the existing <c>ClaimStore</c>) PLUS the structured
/// <c>(</c><see cref="Subject"/><c>, </c><see cref="Attribute"/><c>, </c>
/// <see cref="Value"/><c>)</c> attribution that lets base Mneme index the claim
/// as canon and detect <em>continuity contradictions</em> deterministically —
/// the gap a flat-text "reconcile" pass (an LLM handed the whole codex as a
/// digest) cannot close.
/// </summary>
/// <remarks>
/// <para>
/// Rides under <see cref="EpistemicCategory.Fact"/>: the seven epistemic
/// categories are locked, so a writing-domain fact lives in the append-only log
/// under one of the seven while the profile's projector recognises it by payload
/// type — exactly the pattern <see cref="SkillPayload"/> uses under Evidence.
/// </para>
/// <para>
/// The two bi-temporal axes the writing domain needs already exist on the event
/// envelope: <c>ValidAt</c> carries <em>story-time</em> (when it is true in the
/// narrated world) and <c>RecordedAt</c> carries <em>reveal-time</em> (when the
/// reader/draft learns it). This payload therefore adds no timestamps of its
/// own; the ingesting caller sets those on the <see cref="CaptureEvent"/>.
/// </para>
/// </remarks>
/// <param name="Text">The claim as a single flat declarative sentence (drop-in for MuxiMuxi's <c>Claim.Text</c>).</param>
/// <param name="Subject">The entity the claim is about (e.g. "Helios"). Reduced to a stable subject key for canon indexing.</param>
/// <param name="Attribute">The attribute being asserted (e.g. "eye colour"). Maps to the triple predicate.</param>
/// <param name="Value">The value asserted for the attribute (e.g. "blue"). Maps to the triple object.</param>
/// <param name="ScenePath">Manuscript passage that establishes the claim, if any (drop-in for <c>Claim.ScenePath</c>).</param>
/// <param name="Quote">Verbatim quote anchoring the claim to that passage, if any (drop-in for <c>Claim.Quote</c>).</param>
/// <param name="Status">Lifecycle status of the claim (e.g. "asserted", "verified", "refuted"). Free-form, mirrors MuxiMuxi's string status.</param>
/// <param name="SourceRef">External source this claim is grounded in, if any (a citation/URL) — used when <see cref="Grounding"/> is <see cref="AuthoringGrounding.ExternalSource"/>.</param>
/// <param name="ThreadId">Optional narrative/argument thread this claim belongs to (e.g. a <c>thread:&lt;slug&gt;</c>).</param>
/// <param name="Grounding">Whether truth is judged against internal canon (fiction) or an external source (non-fiction/research).</param>
public sealed record AuthoringClaimPayload(
    string Text,
    string Subject,
    string Attribute,
    string Value,
    string? ScenePath = null,
    string? Quote = null,
    string Status = "asserted",
    string? SourceRef = null,
    string? ThreadId = null,
    AuthoringGrounding Grounding = AuthoringGrounding.InternalCanon)
    : EventPayload
{
    /// <inheritdoc/>
    public override EpistemicCategory Category => EpistemicCategory.Fact;

    /// <summary>
    /// The structured triple this claim asserts. <see cref="Subject"/>,
    /// <see cref="Attribute"/>, and <see cref="Value"/> map to the shared
    /// canon's subject / predicate / object, so base Mneme's contradiction
    /// engine can diff two claims that share a subject + attribute but disagree
    /// on the value.
    /// </summary>
    public FactTriple ToTriple() => new(Subject, Attribute, Value);
}
