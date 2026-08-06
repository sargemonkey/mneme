using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Mcp;

namespace Mneme.Mcp;

/// <summary>
/// Stdio host for Mneme's MCP server. Configuration is environment-variable
/// driven so Claude Desktop / VS Code Copilot integrations can launch this
/// binary directly with a one-line config.
/// </summary>
/// <remarks>
/// Required env vars:
/// <list type="bullet">
///   <item><c>MNEME_WORKSTREAM_ID</c> — the workstream this server is bound to.</item>
///   <item><c>MNEME_SQLITE_PATH</c>  — absolute path to the SQLite database.</item>
///   <item><c>MNEME_USER_ID</c>      — principal id (single-tenant in stdio mode).</item>
/// </list>
/// Optional:
/// <list type="bullet">
///   <item><c>MNEME_CAPABILITY_TOKEN</c> — opaque signature pass-through.</item>
///   <item><c>MNEME_INCLUDE_TECHNICAL</c> — set to "true" to include the technical channel in queries.</item>
/// </list>
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var workstream = Environment.GetEnvironmentVariable("MNEME_WORKSTREAM_ID");
        var sqlitePath = Environment.GetEnvironmentVariable("MNEME_SQLITE_PATH");
        var userId     = Environment.GetEnvironmentVariable("MNEME_USER_ID");
        var includeTechnical = bool.TryParse(
            Environment.GetEnvironmentVariable("MNEME_INCLUDE_TECHNICAL"), out var b) && b;

        if (string.IsNullOrEmpty(workstream) || string.IsNullOrEmpty(sqlitePath) || string.IsNullOrEmpty(userId))
        {
            await Console.Error.WriteLineAsync(
                "Mneme.Mcp stdio host requires MNEME_WORKSTREAM_ID, MNEME_SQLITE_PATH, and MNEME_USER_ID.");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder(args);

        // MCP stdio uses stdout for the protocol — push all logs to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = sqlitePath;
            o.UserId = userId;
            o.IncludeTechnical = includeTechnical;
        });

        // CurationCapability is not part of AddMneme's default; the stdio
        // host trusts the caller (they own the process) and grants every
        // curation flag. HTTP mode will replace this with a per-request
        // claim-based capability.
        builder.Services.AddSingleton(sp => new CurationCapability(
            Principal: new PrincipalId(userId),
            Workstream: new WorkstreamId(workstream),
            NotBefore: DateTimeOffset.UtcNow,
            NotAfter: DateTimeOffset.UtcNow.AddDays(365),
            CanAmend: true, CanAnnotate: true, CanPin: true, CanDemote: true,
            CanSplit: true, CanMerge: true, CanRevert: true, CanReview: true));

        builder.Services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "Mneme.Mcp", Version = ServerVersion() })
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(MnemeMcpTools).Assembly);

        var app = builder.Build();
        await app.RunAsync();
        return 0;
    }

    // Reported MCP server version tracks the assembly (csproj <Version>), so it
    // can never drift from the released package version. Strips any '+build'
    // metadata SourceLink appends to the informational version.
    private static string ServerVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
