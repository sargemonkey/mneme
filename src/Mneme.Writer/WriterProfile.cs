using Mneme.Hosting.Profiles;
using Mneme.Projections;

namespace Mneme.Writer;

/// <summary>
/// The writing-domain <see cref="IMnemeProfile"/> (ADR-0005 / Phase 15). It
/// contributes one payload type (<see cref="AuthoringClaimPayload"/>) and one
/// projector (<see cref="AuthoringCanonProjector"/>) that feeds the <b>shared</b>
/// canon + contradiction substrate. It ships <b>no</b> schema module: the thin
/// slice deliberately reuses base Mneme's <c>projection_fact_triples</c> and
/// <c>memory_contradictions</c> tables, which is what makes deterministic
/// manuscript continuity checking fall out of the base engine for free.
/// </summary>
/// <remarks>
/// Register with <c>services.AddMneme(…).AddMnemeWriterProfile();</c>
/// (see <see cref="MnemeWriterServiceCollectionExtensions"/>).
/// </remarks>
public sealed class WriterProfile : IMnemeProfile
{
    /// <inheritdoc/>
    public string Name => "writer";

    /// <inheritdoc/>
    public IReadOnlyList<IPayloadDescriptor> PayloadDescriptors { get; } =
        new IPayloadDescriptor[] { new AuthoringClaimDescriptor() };

    /// <inheritdoc/>
    public IReadOnlyList<ISchemaModule> SchemaModules { get; } = Array.Empty<ISchemaModule>();

    /// <inheritdoc/>
    public IReadOnlyList<IProjector> Projectors { get; } =
        new IProjector[] { new AuthoringCanonProjector() };
}
