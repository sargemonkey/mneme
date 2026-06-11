namespace Mneme.Contracts;

/// <summary>
/// The Mneme context bundle for a workstream. Two-tier shape:
/// <see cref="Index"/> is always loadable (500-1000 tokens) and the
/// detailed <see cref="Sections"/> are loaded on demand (2-4k tokens each).
/// <see cref="Orientation"/> prepends a one-paragraph "where are we" summary;
/// <see cref="Hints"/> points to events that didn't fit so consumers can
/// re-query for detail.
/// </summary>
/// <remarks>
/// Every bundle carries a staleness contract: <see cref="GeneratedAt"/>,
/// <see cref="EventsCoveredThrough"/>, <see cref="IsStale"/>. Consumers MUST
/// inspect these because Mneme's sync/async split means a bundle returned at
/// time T may not reflect events ingested at T-100ms (those run through the
/// distillation worker after the ingest call returns).
/// </remarks>
/// <param name="Workstream">The workstream this bundle synthesizes.</param>
/// <param name="Orientation">One-paragraph orientation prepend.</param>
/// <param name="Index">Lightweight section index — always loaded.</param>
/// <param name="Sections">Detailed sections — loaded on demand based on which index entries the consumer wants.</param>
/// <param name="Hints">Keyword pointers to events that didn't fit in any section.</param>
/// <param name="GeneratedAt">When the bundle was synthesized.</param>
/// <param name="EventsCoveredThrough">The most recent event id reflected in the bundle. <see cref="EventId.None"/> if empty workstream.</param>
/// <param name="IsStale">Whether the bundle has been superseded by events ingested after <see cref="EventsCoveredThrough"/>.</param>
public sealed record ContextBundle(
    WorkstreamId Workstream,
    OrientationSummary Orientation,
    BundleIndex Index,
    IReadOnlyList<BundleSection> Sections,
    LookupHints Hints,
    DateTimeOffset GeneratedAt,
    EventId EventsCoveredThrough,
    bool IsStale);

/// <summary>
/// Lightweight always-loadable index of the sections in a <see cref="ContextBundle"/>.
/// </summary>
/// <param name="Distiller">Identifier of the distiller that produced the bundle (e.g., model name + prompt hash).</param>
/// <param name="TokenBudget">The token budget the index was synthesized against.</param>
/// <param name="TokenCount">Actual token count of the index.</param>
/// <param name="GeneratedAt">When this index was synthesized.</param>
/// <param name="EventsCoveredThrough">The most recent event id reflected in this index.</param>
/// <param name="SectionRefs">References to the detailed sections (loaded on demand).</param>
public sealed record BundleIndex(
    string Distiller,
    int TokenBudget,
    int TokenCount,
    DateTimeOffset GeneratedAt,
    EventId EventsCoveredThrough,
    IReadOnlyList<BundleSectionRef> SectionRefs);

/// <summary>A pointer to a detailed section in a <see cref="ContextBundle"/>.</summary>
/// <param name="Id">Stable section identifier within the bundle.</param>
/// <param name="Title">Human-readable section title.</param>
/// <param name="Category">The epistemic category this section synthesizes.</param>
/// <param name="TokenCount">Approximate token count for the detailed section.</param>
public sealed record BundleSectionRef(
    string Id,
    string Title,
    EpistemicCategory Category,
    int TokenCount);

/// <summary>
/// A detailed section in a <see cref="ContextBundle"/>. Carries its own
/// staleness metadata so partial-bundle invalidation is possible — re-
/// distilling one section does not require re-distilling everything.
/// </summary>
/// <param name="Id">Stable section identifier within the bundle.</param>
/// <param name="Title">Human-readable section title.</param>
/// <param name="Category">The epistemic category this section synthesizes.</param>
/// <param name="Content">The synthesized markdown content of the section.</param>
/// <param name="Distiller">Identifier of the distiller that produced this section.</param>
/// <param name="GeneratedAt">When this section was synthesized.</param>
/// <param name="EventsCoveredThrough">The most recent event id reflected in this section.</param>
/// <param name="TokenBudget">The token budget the section was synthesized against.</param>
/// <param name="TokenCount">Actual token count of the section content.</param>
/// <param name="Provenance">Source events that contributed to this section.</param>
public sealed record BundleSection(
    string Id,
    string Title,
    EpistemicCategory Category,
    string Content,
    string Distiller,
    DateTimeOffset GeneratedAt,
    EventId EventsCoveredThrough,
    int TokenBudget,
    int TokenCount,
    IReadOnlyList<EventId> Provenance);

/// <summary>
/// One-paragraph "where are we" orientation prepended to a <see cref="ContextBundle"/>.
/// Pattern from Cognee <c>GlobalContextSummary</c>. Orients the consuming
/// LLM before it sees the detailed bullets.
/// </summary>
/// <param name="Paragraph">The orientation paragraph itself (typically 50-150 tokens).</param>
/// <param name="Distiller">Identifier of the distiller that produced it.</param>
/// <param name="GeneratedAt">When this orientation was synthesized.</param>
/// <param name="EventsCoveredThrough">The most recent event id reflected.</param>
public sealed record OrientationSummary(
    string Paragraph,
    string Distiller,
    DateTimeOffset GeneratedAt,
    EventId EventsCoveredThrough);

/// <summary>Container for keyword-to-event pointers that didn't fit in any section.</summary>
/// <param name="Hints">The keyword pointers themselves.</param>
public sealed record LookupHints(IReadOnlyList<LookupHint> Hints);

/// <summary>A single keyword → event pointer for re-query.</summary>
/// <param name="Keyword">Free-text keyword(s) describing what's in the pointed-to event.</param>
/// <param name="Pointer">The event the consumer can re-query for detail.</param>
/// <param name="Context">Short context phrase for disambiguation when multiple hints match a query.</param>
public sealed record LookupHint(
    string Keyword,
    EventId Pointer,
    string Context);
