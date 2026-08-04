using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Ingest.Redaction;
using Mneme.Storage;
using Mneme.Studio;
using Mneme.Studio.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Mneme wiring.
// Database lives under <ContentRoot>/data/mneme.studio.db unless overridden
// via Mneme:DatabasePath in configuration. Directory is created on startup.
var dbPath = builder.Configuration.GetValue<string>("Mneme:DatabasePath")
             ?? Path.Combine(builder.Environment.ContentRootPath, "data", "mneme.studio.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

var factory = new SqliteConnectionFactory(dbPath);
// Initialize schema synchronously so the first request never races with DDL.
using (var bootstrap = factory.Open())
{
    SqliteSchema.Initialize(bootstrap);
}

builder.Services.AddSingleton(factory);
builder.Services.AddSingleton<IRedactor>(_ => new RegexRedactor());
builder.Services.AddSingleton<IContentShapeSelector, AlwaysRedactedContent>();
builder.Services.AddSingleton<Mneme.Classification.IClassifier, Mneme.Classification.RuleBasedClassifier>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<Mneme.Projections.ProjectorPipeline>(sp =>
    new Mneme.Projections.ProjectorPipeline(sp.GetRequiredService<SqliteConnectionFactory>()));
builder.Services.AddSingleton<Mneme.Search.TextSearchService>(sp =>
    new Mneme.Search.TextSearchService(sp.GetRequiredService<SqliteConnectionFactory>()));
builder.Services.AddSingleton<IIngestObserver>(sp =>
    new Mneme.Projections.ProjectorIngestObserver(sp.GetRequiredService<Mneme.Projections.ProjectorPipeline>()));
builder.Services.AddSingleton<IIngestObserver>(sp =>
    new Mneme.Search.TextSearchIngestObserver(sp.GetRequiredService<Mneme.Search.TextSearchService>()));
builder.Services.AddSingleton<IMemoryAgent>(sp => new MemoryAgent(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<IRedactor>(),
    sp.GetRequiredService<IContentShapeSelector>(),
    sp.GetRequiredService<Mneme.Classification.IClassifier>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetServices<IIngestObserver>()));
builder.Services.AddSingleton<Mneme.Revocation.IRevocationService>(sp =>
    new Mneme.Revocation.SqliteRevocationService(
        sp.GetRequiredService<SqliteConnectionFactory>(),
        sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IMemoryQueryAPI>(sp => new Mneme.Query.MemoryQueryApi(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<Mneme.Search.TextSearchService>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IMemoryCurator>(sp => new Mneme.Curation.SqliteMemoryCurator(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ICurationLog>(sp => new Mneme.Curation.SqliteCurationLog(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<TimeProvider>()));
// Studio's default capability token: principal=studio-user, scoped to
// every workstream visible to Studio. Cross-workstream so the Query
// page works against whatever the user picks.
builder.Services.AddSingleton(sp => new CapabilityToken(
    Principal: new PrincipalId("studio-user"),
    Workstream: null,
    NotBefore: DateTimeOffset.UtcNow,
    NotAfter: DateTimeOffset.UtcNow.AddDays(365),
    AllowedCategories: Array.Empty<EpistemicCategory>(),
    CrossWorkstream: true,
    IncludeTechnical: false));
builder.Services.AddSingleton<StudioReadService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Marker for WebApplicationFactory in integration tests (future).</summary>
public partial class Program;
