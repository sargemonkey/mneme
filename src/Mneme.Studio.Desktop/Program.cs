using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Mneme.Classification;
using Mneme.Contracts;
using Mneme.Curation;
using Mneme.Ingest;
using Mneme.Ingest.Redaction;
using Mneme.Projections;
using Mneme.Revocation;
using Mneme.Search;
using Mneme.Storage;
using Mneme.Studio;
using Mneme.Studio.Components;
using PhotinoNET;

namespace Mneme.Studio.Desktop;

/// <summary>
/// Native-window Studio. Hosts the same ASP.NET Core / Blazor Server app
/// the web Studio uses, on a private loopback port, then opens a Photino
/// window pointed at it. The web UI and the desktop UI are literally the
/// same pages (single source of truth). Stopping the window stops the host.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var port = PickFreePort();
        var url  = $"http://127.0.0.1:{port}";

        var dbPath = Path.Combine(AppContext.BaseDirectory, "data", "mneme.studio.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var webHost = BuildWebHost(args, port, dbPath);
        var webRun = webHost.RunAsync();

        // Wait briefly for the host to bind the port before opening the window.
        SpinWait.SpinUntil(() => CanConnect(port), TimeSpan.FromSeconds(10));

        new PhotinoWindow()
            .SetTitle("Mneme.Studio")
            .SetUseOsDefaultSize(false)
            .SetSize(1400, 900)
            .Center()
            .SetResizable(true)
            .Load(new Uri(url))
            .WaitForClose();

        webHost.StopAsync().GetAwaiter().GetResult();
        webRun.GetAwaiter().GetResult();
    }

    private static WebApplication BuildWebHost(string[] args, int port, string dbPath)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var factory = new SqliteConnectionFactory(dbPath);
        using (var bootstrap = factory.Open())
        {
            SqliteSchema.Initialize(bootstrap);
        }

        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton<IRedactor, RegexRedactor>();
        builder.Services.AddSingleton<IContentShapeSelector, AlwaysRedactedContent>();
        builder.Services.AddSingleton<IClassifier, RuleBasedClassifier>();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<ProjectorPipeline>(sp =>
            new ProjectorPipeline(sp.GetRequiredService<SqliteConnectionFactory>()));
        builder.Services.AddSingleton<TextSearchService>(sp =>
            new TextSearchService(sp.GetRequiredService<SqliteConnectionFactory>()));
        builder.Services.AddSingleton<IIngestObserver>(sp =>
            new ProjectorIngestObserver(sp.GetRequiredService<ProjectorPipeline>()));
        builder.Services.AddSingleton<IIngestObserver>(sp =>
            new TextSearchIngestObserver(sp.GetRequiredService<TextSearchService>()));
        builder.Services.AddSingleton<IMemoryAgent>(sp => new MemoryAgent(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<IRedactor>(),
            sp.GetRequiredService<IContentShapeSelector>(),
            sp.GetRequiredService<IClassifier>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetServices<IIngestObserver>()));
        builder.Services.AddSingleton<IRevocationService>(sp =>
            new SqliteRevocationService(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<IMemoryQueryAPI>(sp => new Mneme.Query.MemoryQueryApi(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TextSearchService>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<IMemoryCurator>(sp => new SqliteMemoryCurator(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ICurationLog>(sp => new SqliteCurationLog(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(sp => new CapabilityToken(
            Principal: new PrincipalId("studio-user"),
            Workstream: null,
            NotBefore: DateTimeOffset.UtcNow,
            NotAfter: DateTimeOffset.UtcNow.AddDays(365),
            AllowedCategories: Array.Empty<EpistemicCategory>(),
            CrossWorkstream: true,
            IncludeTechnical: false));
        builder.Services.AddSingleton<StudioReadService>();

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }

    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var client = new TcpClient();
            var t = client.ConnectAsync(IPAddress.Loopback, port);
            return t.Wait(200) && client.Connected;
        }
        catch { return false; }
    }
}
