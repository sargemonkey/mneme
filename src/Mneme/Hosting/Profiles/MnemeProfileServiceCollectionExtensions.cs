using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Projections;
using Mneme.Storage;

namespace Mneme.Hosting.Profiles;

/// <summary>
/// DI ergonomic for registering a Mneme <see cref="IMnemeProfile"/> — the
/// satellite-package composition point (ADR-0005). Call <em>after</em>
/// <see cref="MnemeServiceCollectionExtensions.AddMneme"/>:
/// <code>
/// services.AddMneme(o => { … })
///         .AddMnemeProfile&lt;WriterProfile&gt;();
/// </code>
/// It (1) registers the profile's payload descriptors so the shared serializer,
/// redactor, text index, and summarizer recognise them; (2) applies the
/// profile's schema modules against the already-initialised database; and
/// (3) registers the profile's projectors so both the incremental ingest path
/// and <c>RebuildAll</c> maintain its projections.
/// </summary>
public static class MnemeProfileServiceCollectionExtensions
{
    /// <summary>Register a domain profile by type (must have a public parameterless constructor).</summary>
    public static IServiceCollection AddMnemeProfile<TProfile>(this IServiceCollection services)
        where TProfile : IMnemeProfile, new()
        => services.AddMnemeProfile(new TProfile());

    /// <summary>Register a domain profile instance.</summary>
    public static IServiceCollection AddMnemeProfile(this IServiceCollection services, IMnemeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(profile);

        // 1. Payload descriptors — makes the profile's $type resolvable and
        //    self-describing for redaction / FTS / summary. Idempotent per type.
        foreach (var descriptor in profile.PayloadDescriptors)
        {
            PayloadDescriptorRegistry.Register(descriptor);
        }

        // 2. Schema modules — applied against the factory AddMneme already
        //    registered as a concrete instance (so we can run DDL now, right
        //    after the core schema, exactly like AddMneme does its own init).
        if (profile.SchemaModules.Count > 0)
        {
            var factory = ResolveFactoryInstance(services);
            using var conn = factory.Open();
            foreach (var module in profile.SchemaModules)
            {
                module.ApplyDdl(conn);
                RecordModuleVersion(conn, module);
            }
        }

        // 3. Projectors — registered as IProjector services. AddMneme's
        //    ProjectorPipeline factory appends every DI-registered IProjector to
        //    its built-in list, so these participate in ProcessEvent + RebuildAll.
        foreach (var projector in profile.Projectors)
        {
            services.AddSingleton<IProjector>(projector);
        }

        return services;
    }

    private static SqliteConnectionFactory ResolveFactoryInstance(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(SqliteConnectionFactory));
        if (descriptor?.ImplementationInstance is SqliteConnectionFactory factory)
        {
            return factory;
        }
        throw new InvalidOperationException(
            "AddMnemeProfile requires AddMneme to have run first (no SqliteConnectionFactory is registered). " +
            "Call services.AddMneme(...).AddMnemeProfile<T>().");
    }

    private static void RecordModuleVersion(Microsoft.Data.Sqlite.SqliteConnection conn, ISchemaModule module)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_meta(key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", "module:" + module.Name);
        cmd.Parameters.AddWithValue("$v", module.Version.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
