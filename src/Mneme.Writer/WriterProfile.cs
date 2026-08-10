using Mneme.Hosting.Profiles;
using Mneme.Projections;

namespace Mneme.Writer;

/// <summary>
/// The writing-domain <see cref="IMnemeProfile"/> (ADR-0005 / Phase 15). It
/// contributes two payload types — <see cref="AuthoringClaimPayload"/> (rides the
/// shared canon for continuity) and <see cref="NarrativeCommitmentPayload"/>
/// (its own setup→payoff ledger) — with their descriptors, projectors, and the
/// one schema module the ledger needs. The continuity half ships no schema (it
/// reuses base Mneme's <c>projection_fact_triples</c> / <c>memory_contradictions</c>);
/// the commitment half owns <c>projection_narrative_commitments</c>.
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
        new IPayloadDescriptor[]
        {
            new AuthoringClaimDescriptor(),
            new NarrativeCommitmentDescriptor(),
        };

    /// <inheritdoc/>
    public IReadOnlyList<ISchemaModule> SchemaModules { get; } =
        new ISchemaModule[] { new NarrativeCommitmentSchemaModule() };

    /// <inheritdoc/>
    public IReadOnlyList<IProjector> Projectors { get; } =
        new IProjector[]
        {
            new AuthoringCanonProjector(),
            new NarrativeCommitmentProjector(),
        };
}
