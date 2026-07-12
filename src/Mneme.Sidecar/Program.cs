using Microsoft.AspNetCore.Mvc;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Storage;
using McEventId = Mneme.Contracts.EventId;

namespace Mneme.Sidecar;

/// <summary>
/// Phase 9 — out-of-process Mneme deployment. Standalone ASP.NET Core host
/// that exposes the Mneme stack over HTTP so non-.NET consumers, containers,
/// k8s pods, or remote hosts can talk to a single shared Mneme instance.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var workstream  = Req("MNEME_WORKSTREAM_ID");
        var sqlitePath  = Req("MNEME_SQLITE_PATH");
        var userId      = Req("MNEME_USER_ID");
        var bearerToken = Req("MNEME_BEARER_TOKEN");
        var listenUrls  = Environment.GetEnvironmentVariable("MNEME_HTTP_URLS") ?? "http://0.0.0.0:8080";

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(listenUrls);
        builder.Services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = sqlitePath;
            o.UserId = userId;
        });

        var app = builder.Build();

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/readyz", (SqliteConnectionFactory f) =>
        {
            try
            {
                using var c = f.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT 1";
                _ = cmd.ExecuteScalar();
                return Results.Ok(new { status = "ready", workstream });
            }
            catch (Exception ex)
            {
                return Results.Json(new { status = "not_ready", error = ex.Message }, statusCode: 503);
            }
        });

        var api = app.MapGroup("/v1").AddEndpointFilter(new BearerAuth(bearerToken));

        api.MapPost("/events", async ([FromBody] IngestEventDto dto,
            IMemoryAgent agent, CancellationToken ct) =>
        {
            var evt = dto.ToCaptureEvent(workstream, userId);
            var result = await agent.IngestAsync(evt, ct);
            return Results.Ok(new { event_id = result.EventId.Value, recorded_at = result.RecordedAt, was_duplicate = result.WasDuplicate });
        });        api.MapPost("/queries", async ([FromBody] QueryDto dto, IMemoryQueryAPI query,
            CapabilityToken token, CancellationToken ct) =>
        {
            var spec = new QuerySpec(
                Workstream: new WorkstreamId(workstream),
                FreeText: dto.FreeText,
                AsOf: dto.AsOf,
                Limit: dto.Limit <= 0 ? 25 : dto.Limit);
            var result = await query.QueryAsync(new QueryRequest(spec, Explain: dto.Explain), token, ct);
            return Results.Ok(new
            {
                total_matched = result.TotalMatched,
                explain = result.Explain,
                items = result.Items.Select(i => new
                {
                    event_id = i.EventId.Value,
                    category = i.Category.ToString(),
                    summary = i.Summary,
                    score = i.Score,
                    valid_at = i.ValidAt,
                    recorded_at = i.RecordedAt,
                    details = i.Details,
                }),
            });
        });

        api.MapGet("/recent", async ([FromQuery] int limit, IMemoryQueryAPI query,
            CapabilityToken token, CancellationToken ct) =>
        {
            var items = await query.ListRecentAsync(new WorkstreamId(workstream),
                limit <= 0 ? 25 : limit, token, ct);
            return Results.Ok(items.Select(i => new
            {
                event_id = i.EventId.Value,
                category = i.Category.ToString(),
                summary = i.Summary,
                valid_at = i.ValidAt,
                recorded_at = i.RecordedAt,
            }));
        });

        api.MapPost("/distill", async ([FromBody] DistillDto dto,
            IMemoryQueryAPI query, CapabilityToken token, CancellationToken ct) =>
        {
            var bundle = await query.DistillAsync(new WorkstreamId(workstream),
                new DistillOptions(dto.ForceRefresh, dto.TokenBudget), token, ct);
            return Results.Ok(bundle);
        });

        api.MapPost("/revocations", async ([FromBody] RevokeDto dto,
            Mneme.Revocation.IRevocationService rev, CancellationToken ct) =>
        {
            var result = await rev.RevokeAsync(new McEventId(dto.EventId), new WorkstreamId(workstream),
                new PrincipalId(userId), dto.Reason, ct);
            return Results.Ok(new { event_id = result.EventId.Value, revoked_at = result.RevokedAt,
                already_revoked = result.AlreadyRevoked, body_zeroed = result.BodyZeroed });
        });

        app.Run();
    }

    private static string Req(string name) =>
        Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Env var {name} is required for Mneme.Sidecar.");
}

internal sealed class BearerAuth : IEndpointFilter
{
    private readonly byte[] _expected;
    public BearerAuth(string expected) =>
        _expected = System.Text.Encoding.UTF8.GetBytes("Bearer " + expected);
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var auth = System.Text.Encoding.UTF8.GetBytes(context.HttpContext.Request.Headers.Authorization.ToString());
        // Fixed-time comparison so response latency doesn't leak how many leading
        // bytes of the bearer token matched (timing side-channel on the auth path).
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(auth, _expected))
        {
            return Results.Json(new { error = "unauthorized" }, statusCode: 401);
        }
        return await next(context);
    }
}

public sealed record IngestEventDto(string? EventId, string Content, string Category = "Evidence", string? Rationale = null)
{
    public CaptureEvent ToCaptureEvent(string workstream, string userId)
    {
        var category = Enum.TryParse<EpistemicCategory>(Category, true, out var c) ? c : EpistemicCategory.Evidence;
        var id = string.IsNullOrWhiteSpace(EventId) ? "sidecar-" + Guid.NewGuid().ToString("N") : EventId;
        var now = DateTimeOffset.UtcNow;
        EventPayload payload = category switch
        {
            EpistemicCategory.Evidence   => new EvidencePayload(Content, "sidecar"),
            EpistemicCategory.Fact       => new FactPayload(Content, Array.Empty<McEventId>()),
            EpistemicCategory.Decision   => new DecisionPayload(Content, Rationale ?? "", Array.Empty<McEventId>(), new PrincipalId(userId)),
            EpistemicCategory.Hypothesis => new HypothesisPayload(Content, HypothesisState.Open),
            EpistemicCategory.Goal       => new GoalPayload(Content, GoalState.Active),
            EpistemicCategory.Action     => new ActionPayload(Content, null, null),
            EpistemicCategory.Outcome    => new OutcomePayload(Content, McEventId.None, OutcomePolarity.Neutral),
            _ => new EvidencePayload(Content, "sidecar"),
        };
        return new CaptureEvent(
            new McEventId(id), new WorkstreamId(workstream), EventChannel.Epistemic,
            now, now, payload,
            new CaptureProvenance(new CaptureSourceId("mneme-sidecar"), new PrincipalId(userId)));
    }
}

public sealed record QueryDto(string? FreeText, DateTimeOffset? AsOf, int Limit = 25, bool Explain = false);
public sealed record DistillDto(bool ForceRefresh = false, int? TokenBudget = null);
public sealed record RevokeDto(string EventId, string Reason);
