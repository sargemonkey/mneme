using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Curation;
using Mneme.Hosting;
using Mneme.Revocation;
using Mneme.Storage;

namespace Mneme.Cli;

/// <summary>
/// Mutation CLI for Mneme. The Electron Studio shells out to this binary
/// rather than re-implementing curation logic in JavaScript, which
/// preserves the stale-state guard, capability checks, append-only
/// invariants, and bi-temporal honesty that the C# implementation
/// enforces. Reads stay direct-SQLite from the renderer.
/// </summary>
/// <remarks>
/// Required env vars (mirror Mneme.Mcp): <c>MNEME_WORKSTREAM_ID</c>,
/// <c>MNEME_SQLITE_PATH</c>, <c>MNEME_USER_ID</c>.
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var (sp, workstream, principal) = BuildHost();
            using var _ = sp;

            return args[0].ToLowerInvariant() switch
            {
                "ingest"   => await CmdIngest(sp, workstream, principal, args.Skip(1).ToArray()),
                "revoke"   => await CmdRevoke(sp, workstream, principal, args.Skip(1).ToArray()),
                "annotate" => await CmdAnnotate(sp, workstream, principal, args.Skip(1).ToArray()),
                "pin"      => await CmdPin(sp, workstream, principal, args.Skip(1).ToArray()),
                "demote"   => await CmdDemote(sp, workstream, principal, args.Skip(1).ToArray()),
                "amend"    => await CmdAmend(sp, workstream, principal, args.Skip(1).ToArray()),
                "revert"   => await CmdRevert(sp, workstream, principal, args.Skip(1).ToArray()),
                _ => Fail($"Unknown command '{args[0]}'. Run 'mneme help'."),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static (ServiceProvider sp, WorkstreamId ws, PrincipalId principal) BuildHost()
    {
        var workstream = RequiredEnv("MNEME_WORKSTREAM_ID");
        var sqlitePath = RequiredEnv("MNEME_SQLITE_PATH");
        var userId     = RequiredEnv("MNEME_USER_ID");

        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = sqlitePath;
            o.UserId = userId;
        });
        services.AddSingleton(sp => new CurationCapability(
            Principal: new PrincipalId(userId),
            Workstream: new WorkstreamId(workstream),
            NotBefore: DateTimeOffset.UtcNow.AddMinutes(-1),
            NotAfter: DateTimeOffset.UtcNow.AddDays(365),
            CanAmend: true, CanAnnotate: true, CanPin: true, CanDemote: true,
            CanSplit: true, CanMerge: true, CanRevert: true, CanReview: true));
        var sp = services.BuildServiceProvider();
        return (sp, new WorkstreamId(workstream), new PrincipalId(userId));
    }

    private static async Task<int> CmdIngest(ServiceProvider sp, WorkstreamId ws, PrincipalId who, string[] args)
    {
        var content = ArgValue(args, "--content") ?? throw new ArgumentException("--content required");
        var category = Enum.Parse<EpistemicCategory>(ArgValue(args, "--category") ?? "Evidence", true);
        var agent = sp.GetRequiredService<IMemoryAgent>();
        EventPayload payload = category switch
        {
            EpistemicCategory.Evidence   => new EvidencePayload(content, "cli"),
            EpistemicCategory.Fact       => new FactPayload(content, Array.Empty<EventId>()),
            EpistemicCategory.Decision   => new DecisionPayload(content, ArgValue(args, "--rationale") ?? "", Array.Empty<EventId>(), who),
            EpistemicCategory.Hypothesis => new HypothesisPayload(content, HypothesisState.Open),
            EpistemicCategory.Goal       => new GoalPayload(content, GoalState.Active),
            EpistemicCategory.Action     => new ActionPayload(content, null, ArgValue(args, "--ref")),
            EpistemicCategory.Outcome    => new OutcomePayload(content, EventId.None, OutcomePolarity.Neutral),
            _ => throw new ArgumentException("unsupported category"),
        };
        var id = ArgValue(args, "--event-id") ?? "cli-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var result = await agent.IngestAsync(new CaptureEvent(
            new EventId(id), ws, EventChannel.Epistemic, now, now, payload,
            new CaptureProvenance(new CaptureSourceId("mneme-cli"), who)));
        Out(new { result.EventId.Value, result.RecordedAt, result.WasDuplicate });
        return 0;
    }

    private static async Task<int> CmdRevoke(ServiceProvider sp, WorkstreamId ws, PrincipalId who, string[] args)
    {
        var eventId = ArgValue(args, "--event-id") ?? throw new ArgumentException("--event-id required");
        var reason  = ArgValue(args, "--reason")   ?? throw new ArgumentException("--reason required");
        var rev = sp.GetRequiredService<IRevocationService>();
        var result = await rev.RevokeAsync(new EventId(eventId), ws, who, reason);
        Out(new { event_id = result.EventId.Value, result.RevokedAt, result.AlreadyRevoked, result.BodyZeroed });
        return 0;
    }

    private static async Task<int> CmdAnnotate(ServiceProvider sp, WorkstreamId ws, PrincipalId who, string[] args)
    {
        var eventId = ArgValue(args, "--event-id") ?? throw new ArgumentException("--event-id required");
        var text    = ArgValue(args, "--text")     ?? throw new ArgumentException("--text required");
        var curator = sp.GetRequiredService<IMemoryCurator>();
        var cap = sp.GetRequiredService<CurationCapability>();
        var result = await curator.AnnotateAsync(new EventId(eventId), text, cap);
        Out(new { curation_event_id = result.CurationEventId.Value, result.RecordedAt });
        return 0;
    }

    private static async Task<int> CmdPin(ServiceProvider sp, WorkstreamId ws, PrincipalId who, string[] args)
    {
        var eventId = ArgValue(args, "--event-id") ?? throw new ArgumentException("--event-id required");
        var mult    = float.Parse(ArgValue(args, "--multiplier") ?? "2.0", System.Globalization.CultureInfo.InvariantCulture);
        var curator = sp.GetRequiredService<IMemoryCurator>();
        var cap = sp.GetRequiredService<CurationCapability>();
        var result = await curator.PinAsync(new EventId(eventId), PinScope.Workstream, mult, cap);
        Out(new { curation_event_id = result.CurationEventId.Value });
        return 0;
    }

    private static async Task<int> CmdDemote(ServiceProvider sp, WorkstreamId ws, PrincipalId who, string[] args)
    {
        var eventId = ArgValue(args, "--event-id") ?? throw new ArgumentException("--event-id required");
        var mult    = float.Parse(ArgValue(args, "--multiplier") ?? "0.3", System.Globalization.CultureInfo.InvariantCulture);
        var curator = sp.GetRequiredService<IMemoryCurator>();
        var cap = sp.GetRequiredService<CurationCapability>();
        var result = await curator.DemoteAsync(new EventId(eventId), mult, cap);
        Out(new { curation_event_id = result.CurationEventId.Value });
        return 0;
    }

    private static async Task<int> CmdAmend(ServiceProvider sp, WorkstreamId ws, PrincipalId who, string[] args)
    {
        var eventId  = ArgValue(args, "--event-id")    ?? throw new ArgumentException("--event-id required");
        var content  = ArgValue(args, "--new-content") ?? throw new ArgumentException("--new-content required");
        var rationale = ArgValue(args, "--rationale")   ?? "amend via CLI";
        var curator  = sp.GetRequiredService<IMemoryCurator>();
        var cap      = sp.GetRequiredService<CurationCapability>();
        var factory  = sp.GetRequiredService<SqliteConnectionFactory>();
        var target = new EventId(eventId);
        var hash = PreStateHasher.ComputeHash(factory, target);
        var result = await curator.AmendFactAsync(new FactId(eventId), hash,
            new FactAmendment(content, rationale), cap);
        Out(new { curation_event_id = result.CurationEventId.Value, pre_state_hash = result.PreStateHash });
        return 0;
    }

    private static async Task<int> CmdRevert(ServiceProvider sp, WorkstreamId ws, PrincipalId who, string[] args)
    {
        var curationId = ArgValue(args, "--curation-event-id") ?? throw new ArgumentException("--curation-event-id required");
        var reason     = ArgValue(args, "--reason")             ?? "revert via CLI";
        var curator    = sp.GetRequiredService<IMemoryCurator>();
        var cap        = sp.GetRequiredService<CurationCapability>();
        var result = await curator.RevertCurationAsync(new EventId(curationId), reason, cap);
        Out(new { curation_event_id = result.CurationEventId.Value });
        return 0;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static string RequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Env var {name} is required.");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"mneme: {message}");
        Out(new { ok = false, error = message });
        return 1;
    }

    private static void Out<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false }));

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            mneme — Mneme command-line interface

            Env (required):
              MNEME_WORKSTREAM_ID  workstream this CLI talks to
              MNEME_SQLITE_PATH    absolute SQLite path
              MNEME_USER_ID        principal id

            Commands:
              ingest   --content <text> [--category Evidence|Fact|Decision|...]
                       [--event-id <id>] [--rationale <text>] [--ref <url>]
              revoke   --event-id <id> --reason <text>
              annotate --event-id <id> --text <text>
              pin      --event-id <id> [--multiplier 2.0]
              demote   --event-id <id> [--multiplier 0.3]
              amend    --event-id <id> --new-content <text> [--rationale <text>]
              revert   --curation-event-id <id> [--reason <text>]

            Each command prints a single JSON object to stdout on success;
            errors go to stderr + a JSON {"ok":false,"error":...} on stdout.
            """);
    }
}
