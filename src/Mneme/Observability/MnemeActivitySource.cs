using System.Diagnostics;

namespace Mneme.Observability;

/// <summary>
/// The single <see cref="System.Diagnostics.ActivitySource"/> used by every
/// Mneme component. Activities emitted here are zero-cost when no listener
/// is attached, so the default in-process build pays nothing for tracing
/// unless a host wires up an
/// <see cref="System.Diagnostics.ActivityListener"/> (e.g., the OpenTelemetry
/// SDK).
/// </summary>
/// <remarks>
/// <para>
/// Span names follow GenAI Semantic Conventions v1.37 where applicable:
/// <list type="bullet">
///   <item><c>mneme.ingest.event</c> — sync ingest path on
///         <see cref="Mneme.Ingest.MemoryAgent"/>.</item>
///   <item><c>mneme.redactor.run</c> — secret redaction.</item>
///   <item><c>mneme.classify.run</c> — classification (Phase 2 stub
///         in Phase 1).</item>
///   <item><c>mneme.entity.resolve</c> — entity resolution (Phase 6).</item>
///   <item><c>mneme.distill.run</c> — distillation worker (Phase 5).</item>
///   <item><c>mneme.projection.rebuild</c> — projection rebuilds (Phase 3).</item>
///   <item><c>mneme.query.execute</c> — capability-checked query (Phase 4).</item>
/// </list>
/// </para>
/// <para>
/// In Phase 1 only the first two are populated; the others appear as
/// constants here so later phases have a stable name surface.
/// </para>
/// </remarks>
public static class MnemeActivitySource
{
    /// <summary>Source name. Stable across versions.</summary>
    public const string Name = "Mneme";

    /// <summary>The shared <see cref="ActivitySource"/>.</summary>
    public static ActivitySource Source { get; } = new(Name, version: ThisAssemblyVersion);

    /// <summary>Span name for the sync ingest path.</summary>
    public const string IngestEvent = "mneme.ingest.event";
    /// <summary>Span name for the secret redactor.</summary>
    public const string RedactorRun = "mneme.redactor.run";
    /// <summary>Span name for classification.</summary>
    public const string ClassifyRun = "mneme.classify.run";
    /// <summary>Span name for entity resolution.</summary>
    public const string EntityResolve = "mneme.entity.resolve";
    /// <summary>Span name for the distillation worker.</summary>
    public const string DistillRun = "mneme.distill.run";
    /// <summary>Span name for projection rebuilds.</summary>
    public const string ProjectionRebuild = "mneme.projection.rebuild";
    /// <summary>Span name for the capability-checked query path.</summary>
    public const string QueryExecute = "mneme.query.execute";

    private const string ThisAssemblyVersion = "0.0.1";
}
