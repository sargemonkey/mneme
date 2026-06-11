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
builder.Services.AddSingleton<IRedactor, RegexRedactor>();
builder.Services.AddSingleton<IContentShapeSelector, AlwaysRedactedContent>();
builder.Services.AddSingleton<Mneme.Classification.IClassifier, Mneme.Classification.RuleBasedClassifier>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IMemoryAgent>(sp => new MemoryAgent(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<IRedactor>(),
    sp.GetRequiredService<IContentShapeSelector>(),
    sp.GetRequiredService<Mneme.Classification.IClassifier>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<Mneme.Revocation.IRevocationService>(sp =>
    new Mneme.Revocation.SqliteRevocationService(
        sp.GetRequiredService<SqliteConnectionFactory>(),
        sp.GetRequiredService<TimeProvider>()));
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
