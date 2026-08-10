using Mneme.Projections;

namespace Mneme.Hosting.Profiles;

/// <summary>
/// A Mneme <em>domain profile</em> (ADR-0005): a satellite package
/// (<c>Mneme.Writer</c>, <c>Mneme.Research</c>, …) contributes an ontology on
/// top of base Mneme by bundling three things — the payload types it
/// introduces, the projection tables those payloads populate, and the
/// projectors that keep them current. Registered once at composition time via
/// <c>services.AddMneme(…).AddMnemeProfile&lt;TProfile&gt;()</c>.
/// </summary>
/// <remarks>
/// The base implementation of Mneme's own eight payloads is itself expressible
/// as a profile; a satellite adds to that curated, closed set (it never removes
/// or overrides the built-ins). Everything a profile registers is
/// <em>compiled, trusted code</em> from an app-chosen assembly — which is what
/// keeps the deserialization/redaction/no-raw-SQL safety properties intact
/// (ADR-0005).
/// </remarks>
public interface IMnemeProfile
{
    /// <summary>Stable, human-readable profile name (e.g. "writer").</summary>
    string Name { get; }

    /// <summary>Descriptors for the payload types this profile introduces. May be empty.</summary>
    IReadOnlyList<IPayloadDescriptor> PayloadDescriptors { get; }

    /// <summary>Schema modules that create this profile's projection tables. May be empty.</summary>
    IReadOnlyList<ISchemaModule> SchemaModules { get; }

    /// <summary>Projectors that maintain this profile's projections. May be empty.</summary>
    IReadOnlyList<IProjector> Projectors { get; }
}
