using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Studio.Agent.Components;
using Mneme.Studio.Agent.Memory;
using PhotinoNET;

namespace Mneme.Studio.Agent;

/// <summary>
/// Desktop shell. Hosts a Blazor Server app on a private loopback port (the
/// same Photino-over-in-process-web-host pattern as Mneme.Studio.Desktop),
/// then opens a native window pointed at it. The app is an ACP <em>client</em>
/// that drives a coding agent and feeds the conversation to Mneme's
/// session-distillation pipeline.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--smoke"))
        {
            SmokeAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--copilot-smoke"))
        {
            CopilotSmokeAsync().GetAwaiter().GetResult();
            return;
        }

        var port = PickFreePort();
        var url = $"http://127.0.0.1:{port}";

        var webHost = BuildWebHost(args, port);
        var webRun = webHost.RunAsync();

        SpinWait.SpinUntil(() => CanConnect(port), TimeSpan.FromSeconds(10));

        new PhotinoWindow()
            .SetTitle("Mneme × ACP")
            .SetUseOsDefaultSize(false)
            .SetSize(1500, 950)
            .Center()
            .SetResizable(true)
            .Load(new Uri(url))
            .WaitForClose();

        webHost.StopAsync().GetAwaiter().GetResult();
        webRun.GetAwaiter().GetResult();
    }

    private static WebApplication BuildWebHost(string[] args, int port)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var dbPath = Path.Combine(AppContext.BaseDirectory, "data", "mneme.studio.agent.db");
        ConfigureMneme(builder.Services, dbPath);

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }

    /// <summary>
    /// Shared Mneme + ACP registration used by both the desktop host and the
    /// headless <c>--smoke</c> path.
    /// </summary>
    private static void ConfigureMneme(IServiceCollection services, string dbPath)
    {
        // One-call Mneme wiring. Registers the event log, projections, query
        // API, and a workstream-scoped capability token.
        services.AddMneme(o =>
        {
            o.WorkstreamId = "studio-agent";
            o.SqlitePath = dbPath;
            o.UserId = Environment.UserName;
        });

        // The distillation LLM: real GitHub Copilot over its native ACP server.
        // Unless MNEME_AGENT=mock, the session distiller runs Mneme's extraction
        // logic with Copilot doing the interpretation; it degrades to the offline
        // heuristic distiller automatically if the copilot CLI isn't available.
        var forceMock = string.Equals(
            Environment.GetEnvironmentVariable("MNEME_AGENT"), "mock", StringComparison.OrdinalIgnoreCase);
        if (forceMock)
        {
            services.AddSingleton<ISessionDistiller, HeuristicSessionDistiller>();
        }
        else
        {
            services.AddSingleton<CopilotChatCompletion>();
            services.AddSingleton<IChatCompletion>(sp => sp.GetRequiredService<CopilotChatCompletion>());
            services.AddSingleton<ISessionDistiller>(sp =>
                new LlmSessionDistiller(sp.GetRequiredService<IChatCompletion>()));
            services.AddSingleton<IDistiller>(sp =>
                new LlmBundleDistiller(sp.GetRequiredService<IChatCompletion>()));
        }

        // The ACP-client + distillation orchestrator the UI talks to.
        services.AddSingleton<AgentChatService>();
    }

    /// <summary>
    /// Headless end-to-end check: no window. Drives the ACP agent through a
    /// couple of turns and prints the distilled memory, so the whole
    /// prompt → agent → buffer → distill → query loop can be verified in CI.
    /// </summary>
    private static async Task SmokeAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mneme-acp-smoke-{Guid.NewGuid():n}.db");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        ConfigureMneme(services, dbPath);
        await using var sp = services.BuildServiceProvider();

        var chat = sp.GetRequiredService<AgentChatService>();
        Console.WriteLine($"agent={chat.AgentName} workstream={chat.Workstream.Value} session={chat.Session.Value}");

        // 1) Replay a bundled corpus conversation (LoCoMo shape) turn by turn.
        var corpus = Mneme.Studio.Agent.Corpus.CorpusLoader.Load();
        Console.WriteLine($"\ncorpus: {corpus.Count} conversation(s) from {Mneme.Studio.Agent.Corpus.CorpusLoader.ResolvePath()}");
        var convo = corpus.FirstOrDefault(c => c.Id == "demo-project") ?? corpus.FirstOrDefault();
        if (convo is not null)
        {
            var turns = convo.Turns.Take(6).ToList();
            Console.WriteLine($"replaying '{convo.Id}' (first {turns.Count} turns; LLM distill each)…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var turn in turns)
            {
                var r = await chat.FeedEntryAsync(turn.Speaker, turn.Text, turn.At);
                Console.WriteLine($"  {turn.Speaker}: {turn.Text[..Math.Min(60, turn.Text.Length)]}…  → +{r.NewlyDistilled} ({sw.ElapsedMilliseconds} ms)");
                sw.Restart();
            }
        }

        var memory = await chat.GetMemoryAsync();
        Console.WriteLine($"\n=== captured memory ({memory.Count}) ===");
        foreach (var m in memory)
        {
            Console.WriteLine($"  [{m.Category}] {m.Summary}");
        }

        // 2) Reject (revoke) the first captured memory — it should disappear.
        if (memory.Count > 0)
        {
            await chat.RejectMemoryAsync(memory[0].EventId);
            var after = await chat.GetMemoryAsync();
            Console.WriteLine($"\nrejected 1 memory → {memory.Count} then {after.Count} remaining");
        }

        // 3) Sleep: condense everything into a bundle (orientation + sections).
        var bundle = await chat.SleepAsync();
        Console.WriteLine($"\n=== 😴 condensed memory ({bundle.Index.Distiller}) ===");
        Console.WriteLine($"orientation: {bundle.Orientation.Paragraph}");
        foreach (var s in bundle.Sections)
        {
            Console.WriteLine($"  -- {s.Title} ({s.Category}, ~{s.TokenCount} tok) --");
            Console.WriteLine($"     {s.Content.Replace("\n", "\n     ")}");
        }

        await chat.DisposeAsync();
        try { File.Delete(dbPath); } catch { /* best effort */ }
    }

    /// <summary>
    /// Verify the real <c>copilot --acp</c> transport: a plain prompt round-trip
    /// plus an extraction-style JSON prompt (the shape the LLM distiller uses).
    /// </summary>
    private static async Task CopilotSmokeAsync()
    {
        await using var conn = new Mneme.Studio.Agent.Acp.AcpAgentConnection();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("starting copilot --acp …");
        await conn.StartCopilotAsync(Environment.CurrentDirectory);
        Console.WriteLine($"handshake ok: agent={conn.AgentName} ({sw.ElapsedMilliseconds} ms)");

        sw.Restart();
        var reply = await conn.PromptAsync("In one short sentence, what is the capital of France? No tools.");
        Console.WriteLine($"\n[plain prompt] {sw.ElapsedMilliseconds} ms\n{reply}");

        sw.Restart();
        var extractPrompt =
            "Extract durable facts from this conversation as JSON only, no prose, no tools.\n" +
            "Reply exactly: {\"facts\":[{\"category\":\"Decision|Fact|Goal\",\"statement\":\"...\"}]}\n\n" +
            "Conversation:\n" +
            "Priya: We decided to use PostgreSQL over DynamoDB because the team knows SQL.\n" +
            "Sam: Our goal is to move checkout off the monolith by end of Q2.";
        var json = await conn.PromptAsync(extractPrompt);
        Console.WriteLine($"\n[extraction prompt] {sw.ElapsedMilliseconds} ms\n{json}");
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
