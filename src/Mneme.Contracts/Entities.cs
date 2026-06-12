namespace Mneme.Contracts;

/// <summary>
/// Identity-kind discriminator that drives the Tier 1 canonicalization
/// rules in <c>Mneme.Resolution.EntityCanonicalizer</c>. Adding a new
/// kind means: add the enum member, add a canonicalization case, decide
/// whether the kind is eligible for deterministic auto-merge.
/// </summary>
public enum EntityKind
{
    /// <summary>Person, organization, or place with no machine-readable identifier (only a display name). NEVER auto-merges on Tier 1.</summary>
    Name = 0,

    /// <summary>Email address (RFC 5321 mailbox). Lower-cased; gmail.com addresses have dots stripped from the local part.</summary>
    Email = 1,

    /// <summary>GitHub login (user or organization). As-is; GitHub itself is case-insensitive but preserves display case.</summary>
    GitHubLogin = 2,

    /// <summary>Linear identifier (user or team). As-is.</summary>
    LinearId = 3,

    /// <summary>Slack user/channel id. As-is.</summary>
    SlackId = 4,

    /// <summary>Stripe customer/object id. As-is.</summary>
    StripeId = 5,

    /// <summary>URL — normalized to lowercase scheme + host, default ports stripped, trailing-slash on bare paths.</summary>
    Url = 6,

    /// <summary>Free-text identifier the SDK does not recognise. NEVER auto-merges on Tier 1.</summary>
    Other = 99,
}

/// <summary>
/// One resolved entity in the <c>entity_index</c> projection. Every entity
/// has a stable <see cref="EntityId"/> derived from its Tier 1 canonical
/// key (when one exists) — re-asserting the same identity yields the same
/// <see cref="EntityId"/>.
/// </summary>
/// <param name="EntityId">Stable entity id (UUID5 derived from canonical key for keyed kinds; opaque uuid otherwise).</param>
/// <param name="Kind">Identity kind that drove canonicalization.</param>
/// <param name="CanonicalKey">The canonical form of the identifier the Tier 1 hash was computed from. Empty when there isn't one (Name / Other).</param>
/// <param name="DisplayName">Human-readable label preserved verbatim from the first mention. Updated by merges.</param>
/// <param name="Workstream">Workstream this entity belongs to.</param>
/// <param name="FirstSeenAt">When the entity was first asserted.</param>
/// <param name="LastSeenAt">Most recent mention timestamp.</param>
/// <param name="MentionCount">Cached count for popularity dampening.</param>
public sealed record Entity(
    EntityId EntityId,
    EntityKind Kind,
    string CanonicalKey,
    string DisplayName,
    WorkstreamId Workstream,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int MentionCount);

/// <summary>One mention of an entity tied back to a source event.</summary>
/// <param name="EntityId">The entity being mentioned.</param>
/// <param name="EventId">Source event the mention came from.</param>
/// <param name="AssertedDisplayName">The display string as it appeared at the mention site (pre-canonicalization).</param>
/// <param name="At">When the mention occurred.</param>
public sealed record EntityMention(
    EntityId EntityId,
    EventId EventId,
    string AssertedDisplayName,
    DateTimeOffset At);

/// <summary>A consolidated merge of two or more entities into a single canonical id (Tier 3 confirmed).</summary>
/// <param name="WinnerId">The surviving canonical entity id.</param>
/// <param name="LoserIds">Entity ids that were superseded.</param>
/// <param name="ConfirmedBy">Principal who confirmed the merge.</param>
/// <param name="ConfirmedAt">When the merge was recorded.</param>
/// <param name="Rationale">Free-text reason.</param>
public sealed record EntityMerge(
    EntityId WinnerId,
    IReadOnlyList<EntityId> LoserIds,
    PrincipalId ConfirmedBy,
    DateTimeOffset ConfirmedAt,
    string Rationale);

/// <summary>A pending Tier 3 merge proposal waiting on human confirmation.</summary>
/// <param name="ProposalId">Opaque proposal id (used by the confirm/reject API).</param>
/// <param name="WinnerId">The proposer's preferred surviving id.</param>
/// <param name="LoserIds">Entities the proposer thinks should fold into the winner.</param>
/// <param name="Confidence">Proposer's self-assessed confidence in <c>[0,1]</c>.</param>
/// <param name="Rationale">Why the proposer thinks these should merge.</param>
/// <param name="ProposedBy">Identifier of the proposer (e.g., <c>"llm-judge/gpt-4o@2026-06"</c>).</param>
/// <param name="ProposedAt">When the proposal was created.</param>
/// <param name="WinnerStateHash">Hash of the winner's canonical state at proposal time. Re-verified at confirm time to block stale confirmations.</param>
public sealed record EntityMergeProposal(
    string ProposalId,
    EntityId WinnerId,
    IReadOnlyList<EntityId> LoserIds,
    double Confidence,
    string Rationale,
    string ProposedBy,
    DateTimeOffset ProposedAt,
    string WinnerStateHash);

/// <summary>
/// Result of asserting an entity into the resolver. Tells the caller
/// which tier matched and whether a new row was created.
/// </summary>
/// <param name="Entity">The resolved entity (existing or newly-created).</param>
/// <param name="Tier">Which tier matched.</param>
/// <param name="WasNew">True if the entity was created by this call.</param>
public sealed record EntityResolution(
    Entity Entity,
    EntityResolutionTier Tier,
    bool WasNew);

/// <summary>Which tier matched in a Tier 1/2/3 entity-resolution call.</summary>
public enum EntityResolutionTier
{
    /// <summary>Deterministic UUID5 key match (auto-merge).</summary>
    Deterministic = 1,
    /// <summary>Embedding similarity ≥0.95 (auto-merge — requires <see cref="IEmbeddingProvider"/>).</summary>
    Embedding = 2,
    /// <summary>LLM-proposed match awaiting human confirmation (NOT auto-merge — recorded as proposal only).</summary>
    LlmProposed = 3,
    /// <summary>No match — a new entity was created.</summary>
    New = 4,
}

/// <summary>
/// Host-supplied LLM judge for Tier 3 merge proposals. Symmetric to
/// <see cref="IDistiller"/> — the SDK assembles candidate pairs and the
/// host decides whether to propose a merge. The SDK does <strong>not</strong>
/// auto-apply LLM judgments; every proposal flows through a propose-then-
/// confirm pipeline.
/// </summary>
public interface IEntityProposer
{
    /// <summary>Stable identifier stamped onto every proposal.</summary>
    string Id { get; }

    /// <summary>
    /// Inspect candidate pairs (no Tier 1/2 match found) and return zero
    /// or more proposals. Pairs returned with low <see cref="EntityMergeProposal.Confidence"/>
    /// (&lt;0.5) are discarded by the SDK; high-confidence proposals are
    /// persisted to <c>entity_merge_proposals</c> for human review.
    /// </summary>
    Task<IReadOnlyList<EntityMergeProposal>> ProposeAsync(
        IReadOnlyList<EntityMergeCandidatePair> candidates,
        CancellationToken ct = default);
}

/// <summary>One pair the resolver wants the host's <see cref="IEntityProposer"/> to score.</summary>
public sealed record EntityMergeCandidatePair(
    Entity Left,
    Entity Right);
