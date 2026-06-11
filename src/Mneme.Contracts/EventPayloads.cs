using System.Text.Json.Serialization;

namespace Mneme.Contracts;

/// <summary>
/// Polymorphic base for the payload of a <see cref="CaptureEvent"/>. Every
/// concrete payload type is one of the seven epistemic categories
/// (see <see cref="EpistemicCategory"/>) so the discriminator is
/// implicit and stable.
/// </summary>
/// <remarks>
/// The <c>$type</c> property discriminator is automatically serialized by
/// <see cref="System.Text.Json"/> via <see cref="JsonDerivedTypeAttribute"/>.
/// Add new payload kinds by adding a sealed record below + a
/// <see cref="JsonDerivedTypeAttribute"/> entry here.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(EvidencePayload), nameof(EvidencePayload))]
[JsonDerivedType(typeof(FactPayload), nameof(FactPayload))]
[JsonDerivedType(typeof(DecisionPayload), nameof(DecisionPayload))]
[JsonDerivedType(typeof(HypothesisPayload), nameof(HypothesisPayload))]
[JsonDerivedType(typeof(GoalPayload), nameof(GoalPayload))]
[JsonDerivedType(typeof(ActionPayload), nameof(ActionPayload))]
[JsonDerivedType(typeof(OutcomePayload), nameof(OutcomePayload))]
public abstract record EventPayload
{
    /// <summary>The epistemic category this payload belongs to. Set by each derived record.</summary>
    [JsonIgnore]
    public abstract EpistemicCategory Category { get; }
}

/// <summary>A raw observation captured by a source — chat turn, document excerpt, signal payload.</summary>
/// <param name="Content">Textual content (post-redaction).</param>
/// <param name="Source">Free-text source descriptor (URL, file path, plugin name, etc.).</param>
/// <param name="Classification">Sensitivity classification.</param>
public sealed record EvidencePayload(
    string Content,
    string? Source,
    Classification Classification = Classification.Public)
    : EventPayload
{
    /// <inheritdoc/>
    public override EpistemicCategory Category => EpistemicCategory.Evidence;
}

/// <summary>A synthesized atomic claim derived from evidence.</summary>
/// <param name="Statement">The fact as a single declarative sentence.</param>
/// <param name="SupportingEvents">Events that support this fact. May be empty when ingested directly.</param>
public sealed record FactPayload(
    string Statement,
    IReadOnlyList<EventId> SupportingEvents)
    : EventPayload
{
    /// <inheritdoc/>
    public override EpistemicCategory Category => EpistemicCategory.Fact;
}

/// <summary>A choice made by a human or agent, with rationale and supporting evidence.</summary>
/// <param name="Statement">Human-readable summary of the decision.</param>
/// <param name="Rationale">Why this choice was made.</param>
/// <param name="SupportingEvents">Events that informed this decision.</param>
/// <param name="Approver">Principal who approved (typically a human).</param>
public sealed record DecisionPayload(
    string Statement,
    string Rationale,
    IReadOnlyList<EventId> SupportingEvents,
    PrincipalId Approver)
    : EventPayload
{
    /// <inheritdoc/>
    public override EpistemicCategory Category => EpistemicCategory.Decision;
}

/// <summary>A claim under investigation. State machine: open → confirmed | refuted | abandoned.</summary>
/// <param name="Statement">The hypothesis as a single declarative sentence.</param>
/// <param name="State">Current state of the hypothesis.</param>
public sealed record HypothesisPayload(
    string Statement,
    HypothesisState State)
    : EventPayload
{
    /// <inheritdoc/>
    public override EpistemicCategory Category => EpistemicCategory.Hypothesis;
}

/// <summary>States in the hypothesis lifecycle.</summary>
public enum HypothesisState
{
    /// <summary>Newly opened; investigation in progress.</summary>
    Open = 0,
    /// <summary>Supported by evidence.</summary>
    Confirmed = 1,
    /// <summary>Contradicted by evidence.</summary>
    Refuted = 2,
    /// <summary>Closed without resolution.</summary>
    Abandoned = 3,
}

/// <summary>An outcome a workstream is pursuing. State machine: active → achieved | abandoned.</summary>
/// <param name="Statement">The goal as a single declarative sentence.</param>
/// <param name="State">Current state of the goal.</param>
public sealed record GoalPayload(
    string Statement,
    GoalState State)
    : EventPayload
{
    /// <inheritdoc/>
    public override EpistemicCategory Category => EpistemicCategory.Goal;
}

/// <summary>States in the goal lifecycle.</summary>
public enum GoalState
{
    /// <summary>Currently being pursued.</summary>
    Active = 0,
    /// <summary>Completed successfully.</summary>
    Achieved = 1,
    /// <summary>Closed without completion.</summary>
    Abandoned = 2,
}

/// <summary>An executed step that affects the outside world. Links back to the deciding event.</summary>
/// <param name="Statement">Human-readable summary of what was done.</param>
/// <param name="DecisionEvent">The decision this action implements, if any.</param>
/// <param name="ExternalReference">External system identifier (PR URL, ticket id, email message-id).</param>
public sealed record ActionPayload(
    string Statement,
    EventId? DecisionEvent,
    string? ExternalReference)
    : EventPayload
{
    /// <inheritdoc/>
    public override EpistemicCategory Category => EpistemicCategory.Action;
}

/// <summary>An observation about what happened after an action — closes the action → decision loop.</summary>
/// <param name="Statement">Human-readable summary of the observed outcome.</param>
/// <param name="ActionEvent">The action this outcome closes.</param>
/// <param name="Polarity">Whether the outcome was favorable, unfavorable, or neutral.</param>
public sealed record OutcomePayload(
    string Statement,
    EventId ActionEvent,
    OutcomePolarity Polarity)
    : EventPayload
{
    /// <inheritdoc/>
    public override EpistemicCategory Category => EpistemicCategory.Outcome;
}

/// <summary>How an outcome reflects on the action that produced it.</summary>
public enum OutcomePolarity
{
    /// <summary>Outcome was unfavorable.</summary>
    Negative = -1,
    /// <summary>Outcome was neither favorable nor unfavorable.</summary>
    Neutral = 0,
    /// <summary>Outcome was favorable.</summary>
    Positive = 1,
}
