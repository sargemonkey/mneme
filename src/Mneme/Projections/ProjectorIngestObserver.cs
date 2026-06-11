using Mneme.Ingest;
using Mneme.Projections;

namespace Mneme.Projections;

/// <summary>Bridges <see cref="ProjectorPipeline"/> into the <see cref="IIngestObserver"/> chain.</summary>
public sealed class ProjectorIngestObserver : IIngestObserver
{
    private readonly ProjectorPipeline _pipeline;
    public ProjectorIngestObserver(ProjectorPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
    }
    /// <inheritdoc/>
    public void OnIngested(EventEnvelope envelope) => _pipeline.ProcessEvent(envelope);
}
