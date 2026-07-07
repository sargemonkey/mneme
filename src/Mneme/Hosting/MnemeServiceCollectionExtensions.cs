using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mneme.Classification;
using Mneme.Contracts;
using Mneme.Curation;
using Mneme.Ingest;
using Mneme.Ingest.Redaction;
using Mneme.Ingest.Validation;
using Mneme.Outcomes;
using Mneme.Projections;
using Mneme.Query;
using Mneme.Resolution;
using Mneme.Revocation;
using Mneme.Search;
using Mneme.Sessions;
using Mneme.Storage;

namespace Mneme.Hosting;

/// <summary>
/// One-call DI ergonomic for hosting Mneme inside a .NET app:
/// <code>
/// services.AddMneme(opts =>
/// {
///     opts.WorkstreamId = "demo";
///     opts.SqlitePath   = "data/mneme.db";
///     opts.UserId       = "alice";
/// });
/// </code>
/// Registers the storage factory, schema initialization (idempotent),
/// redactor, classifier, content-shape selector, time provider, agent,
/// revocation service, and a derived <see cref="CapabilityToken"/>.
/// </summary>
/// <remarks>
/// Source guidance: <c>plans/research-design-lessons.md §3.5 + §4.9</c>.
/// Full <see cref="CapabilityToken"/> construction remains available for
/// cross-workstream scenarios — supply your own via
/// <c>services.AddSingleton&lt;CapabilityToken&gt;(...)</c> after
/// <see cref="AddMneme"/>.
/// </remarks>
public static class MnemeServiceCollectionExtensions
{
    /// <summary>Register the Mneme stack with the supplied options.</summary>
    public static IServiceCollection AddMneme(
        this IServiceCollection services,
        Action<MnemeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var opts = new MnemeOptions();
        configure(opts);

        if (string.IsNullOrEmpty(opts.WorkstreamId))
        {
            throw new ArgumentException("MnemeOptions.WorkstreamId is required.", nameof(configure));
        }
        if (string.IsNullOrEmpty(opts.SqlitePath))
        {
            throw new ArgumentException("MnemeOptions.SqlitePath is required.", nameof(configure));
        }
        if (string.IsNullOrEmpty(opts.UserId))
        {
            throw new ArgumentException("MnemeOptions.UserId is required.", nameof(configure));
        }
        WorkstreamIdValidator.EnsureValid(opts.WorkstreamId, nameof(opts.WorkstreamId));

        var directory = Path.GetDirectoryName(opts.SqlitePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var factory = new SqliteConnectionFactory(opts.SqlitePath);
        using (var bootstrap = factory.Open())
        {
            SqliteSchema.Initialize(bootstrap);
        }

        services.AddSingleton(factory);
        services.TryAddSingleton<IRedactor, RegexRedactor>();
        services.TryAddSingleton<IContentShapeSelector, AlwaysRedactedContent>();
        services.TryAddSingleton<IClassifier, RuleBasedClassifier>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<ProjectorPipeline>(sp => new ProjectorPipeline(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            new IProjector[]
            {
                new Mneme.Projections.Projectors.FactsProjector(),
                new Mneme.Projections.Projectors.FactTriplesProjector(),
                new Mneme.Projections.Projectors.DecisionsProjector(),
                new Mneme.Projections.Projectors.GoalsProjector(),
                new Mneme.Projections.Projectors.HypothesesProjector(),
                new DecisionChainsProjector(),
            }));
        services.TryAddSingleton<TextSearchService>(sp => new TextSearchService(
            sp.GetRequiredService<SqliteConnectionFactory>()));
        services.TryAddSingleton<VectorIndex>(sp => new VectorIndex(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetService<IEmbeddingProvider>(),
            sp.GetRequiredService<TimeProvider>(),
            TimeSpan.FromDays(30)));
        services.AddSingleton<IIngestObserver>(sp => new ProjectorIngestObserver(
            sp.GetRequiredService<ProjectorPipeline>()));
        services.AddSingleton<IIngestObserver>(sp => new TextSearchIngestObserver(
            sp.GetRequiredService<TextSearchService>()));
        services.TryAddSingleton<IMemoryAgent>(sp => new MemoryAgent(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<IRedactor>(),
            sp.GetRequiredService<IContentShapeSelector>(),
            sp.GetRequiredService<IClassifier>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetServices<IIngestObserver>(),
            sp.GetService<ISessionDistiller>()));
        services.TryAddSingleton<IRevocationService>(sp => new SqliteRevocationService(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IMemoryQueryAPI>(sp => new MemoryQueryApi(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TextSearchService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IDistiller>(),
            sp.GetRequiredService<VectorIndex>(),
            sp.GetService<IReranker>(),
            opts.SubjectAttributionBoost));
        services.TryAddSingleton<IMemoryCurator>(sp => new SqliteMemoryCurator(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ICurationLog>(sp => new SqliteCurationLog(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<EntityResolver>(sp => new EntityResolver(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetService<IEmbeddingProvider>(),
            sp.GetService<IEntityProposer>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<FeedbackLearner>(sp => new FeedbackLearner(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IIngestObserver>(sp => new FeedbackIngestObserver(
            sp.GetRequiredService<FeedbackLearner>(),
            sp.GetRequiredService<SqliteConnectionFactory>()));

        // Session distillation coordinator. Always registered; throws a
        // clear message at call time if no ISessionDistiller is wired.
        services.TryAddSingleton<SessionDistillationCoordinator>(sp =>
            new SessionDistillationCoordinator(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<IMemoryAgent>(),
                sp.GetService<ISessionDistiller>(),
                sp.GetRequiredService<TimeProvider>()));

        var permitted = opts.PermittedCategories?.ToArray()
            ?? Array.Empty<EpistemicCategory>(); // empty == all (per CapabilityToken.Allows)
        var now = DateTimeOffset.UtcNow;
        var token = new CapabilityToken(
            Principal: new PrincipalId(opts.UserId),
            Workstream: new WorkstreamId(opts.WorkstreamId),
            NotBefore: now,
            NotAfter: now + opts.TokenLifetime,
            AllowedCategories: permitted,
            CrossWorkstream: false,
            IncludeTechnical: opts.IncludeTechnical);
        services.TryAddSingleton(token);

        return services;
    }
}
