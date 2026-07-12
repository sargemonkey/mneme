# Mneme — Usage Guide

End-to-end walkthrough of wiring Mneme into a host. Pairs with
[ARCHITECTURE.md](ARCHITECTURE.md), which explains *why* the API looks
the way it does.

---

## 1. Install

Mneme targets **.NET 8+**. One package gives you the memory store — the
BCL-only `Mneme.Contracts` surface is bundled in:

```pwsh
dotnet add package Mneme --prerelease
```

Optional integrations:

```pwsh
dotnet add package Mneme.Agents.AI --prerelease   # Microsoft Agent Framework
dotnet tool install -g Mneme.Mcp --prerelease     # MCP server tool (mneme-mcp)
```

Building against a local checkout instead? Reference the projects directly:

```pwsh
dotnet add reference path/to/mneme/src/Mneme/Mneme.csproj
dotnet add reference path/to/mneme/src/Mneme.Agents.AI/Mneme.Agents.AI.csproj  # optional
```

---

## 2. Five-line wire-up

```csharp
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;

var services = new ServiceCollection();
services.AddMneme(o =>
{
    o.WorkstreamId = "my-team-q3-2026";
    o.SqlitePath   = Path.Combine(AppContext.BaseDirectory, "data", "mneme.db");
    o.UserId       = "alice@contoso.com";
});

await using var sp = services.BuildServiceProvider();
var agent = sp.GetRequiredService<IMemoryAgent>();
var query = sp.GetRequiredService<IMemoryQueryAPI>();
var token = sp.GetRequiredService<CapabilityToken>();
```

`AddMneme` registers:

- The storage layer (`SqliteConnectionFactory`, schema initialization).
- Redactor, classifier, content-shape selector.
- `IMemoryAgent`, `IMemoryQueryAPI`, `IMemoryCurator`,
  `IRevocationService`, `ICurationLog`.
- Projector pipeline + text-search service + ingest observers.
- Entity resolver + feedback learner.
- A default `CapabilityToken` derived from `UserId` + `WorkstreamId`.
- The `SessionDistillationCoordinator` (waits for an
  `ISessionDistiller` registration; throws clearly at call time if none
  is wired).

---

## 3. The two ingest paths

### 3a. Session distillation (the primary path)

Use this when the host has a session with a growing context
(conversation turns, file reads, tool outputs, sub-agent results) and
wants Mneme to interpret the new tail since the last call.

```csharp
// Register your distiller.
services.AddSingleton<ISessionDistiller>(_ => new MySessionDistiller(myChatClient));

// Hand Mneme the entries; it'll filter to the tail past the watermark.
var session = new SessionId("session-42");
var entries = new[]
{
    new ContextEntry("0001", DateTimeOffset.UtcNow.AddMinutes(-30),
        ContextEntryKind.UserMessage,      "I'm prepping for the Q3 review."),
    new ContextEntry("0002", DateTimeOffset.UtcNow.AddMinutes(-29),
        ContextEntryKind.AssistantMessage, "Anything specific to remember?"),
    new ContextEntry("0003", DateTimeOffset.UtcNow.AddMinutes(-28),
        ContextEntryKind.UserMessage,      "We decided to ship in October, not September."),
};
var result = await agent.DistillSessionAsync(session, entries, token);

Console.WriteLine($"Distilled {result.NewEvents.Count} new event(s); " +
                  $"watermark now at entry {result.NewWatermark.LastDistilledEntryId}.");
```

What the host owes:

- A monotonically-increasing `EntryId` per entry within a session.
  ULIDs work; zero-padded ordinals work (`"0001"`, `"0002"`, …).
  The id is compared with `string.CompareOrdinal`.
- The original chat-log range — Mneme stores no copy. When a caller
  later asks "why does memory say X?", the host re-fetches the cited
  range from its own log.
- An `ISessionDistiller` implementation (see §4).

Idempotency: re-calling with the same `(session, from-id, to-id)` is a
no-op (`result.WasNoOp == true`). The host can safely retry without
duplicating events.

Watermark inspection:

```csharp
var w = await agent.GetWatermarkAsync(session);
// null  → session has never been distilled.
// non-null → pass entries strictly after w.LastDistilledEntryId next time.
```

### 3b. Direct ingest (for pre-shaped events)

Use this when the event is already in epistemic shape — a workflow run
emitted by your CI, a webhook, a user typing into MCP's `remember`
tool, a curation. No LLM needed.

```csharp
var envelope = new CaptureEvent(
    EventId:      new EventId("ci-build-4811-failed"),
    WorkstreamId: token.Workstream!.Value,
    Channel:      EventChannel.Epistemic,
    ValidAt:      DateTimeOffset.UtcNow,
    RecordedAt:   DateTimeOffset.UtcNow,
    Payload:      new OutcomePayload(
        Statement:    "Build #4811 failed on the release branch",
        ActionEvent:  EventId.None,
        Polarity:     OutcomePolarity.Negative),
    Provenance:   new CaptureProvenance(
        Source:    new CaptureSourceId("github-actions"),
        Principal: token.Principal,
        Context:   "release-pipeline",
        Citation:  new Citation.Workflow("github-actions", "4811", Step: "test")));

var ingest = await agent.IngestAsync(envelope);
```

---

## 4. Implementing `ISessionDistiller`

The distiller decides which entries become events. It's host-owned, so
you can use any model, any prompt, any heuristic.

```csharp
using Microsoft.Extensions.AI;   // IChatClient

public sealed class MySessionDistiller : ISessionDistiller
{
    private readonly IChatClient _chat;
    public MySessionDistiller(IChatClient chat) { _chat = chat; }
    public string Id => "mycompany/session-distiller-v3@2026-06";

    private const string SystemPrompt = """
        Extract durable memory from a conversation slice. Worth remembering =
        a Fact, Decision, Goal, Hypothesis, Action, or Outcome. Small talk:
        skip. Reply with JSON:
        {
          "events":  [{"category":"Decision","content":"...","supporting":["0003"]}],
          "dropped": [{"entry_id":"0005","reason":"small talk"}]
        }
        category ∈ {Evidence, Fact, Decision, Hypothesis, Goal, Action, Outcome}.
        Empty events array is fine.
        """;

    public async Task<SessionDistillationResult> DistillAsync(
        SessionDistillationRequest req, CancellationToken ct = default)
    {
        var user = string.Join('\n', req.Entries.Select(e =>
            $"{e.EntryId} {e.Kind} {e.Text}"));
        var response = await _chat.GetResponseAsync(
            [new(ChatRole.System, SystemPrompt), new(ChatRole.User, user)],
            new ChatOptions { Temperature = 0 }, ct);
        return ParseLlmReply(response.Text);
    }

    private static SessionDistillationResult ParseLlmReply(string? text) { /* … */ }
}
```

Hints:

- Stamp your prompt revision into `Id` (e.g.
  `"mycompany/session-distiller-v3@2026-06"`). Mneme records it on the
  watermark + every event's provenance so you can tell which events came
  from which prompt.
- `request.PriorFacts` is a small set of already-distilled facts in the
  workstream — surface them in the prompt so the distiller can avoid
  duplicating or can spot supersessions.
- `request.TokenBudget` is a soft cap; respect it but the SDK won't
  enforce.
- Each `DistilledEvent.SupportingEntryIds` is what Mneme uses to build
  the `Citation.SessionRange`. Always cite at least one supporting id.

---

## 5. Reading from Mneme

### 5a. Ranked query

```csharp
var spec = new QuerySpec(
    Workstream: token.Workstream,
    FreeText:   "October ship date",
    Categories: new[] { EpistemicCategory.Decision, EpistemicCategory.Outcome },
    Limit:      10);
var hits = await query.QueryAsync(new QueryRequest(spec, Explain: true), token);

foreach (var hit in hits.Items)
{
    Console.WriteLine($"[{hit.Category}] {hit.Summary} (score {hit.Score:F2})");
}
```

Score is the fused (BM25 × recency × curation multipliers); set
`Explain: true` to get the per-component breakdown in
`hits.Explain`.

### 5b. Point-in-time (`AsOf`)

```csharp
var asYesterday = new QuerySpec(
    Workstream: token.Workstream,
    FreeText:   "ship date",
    AsOf:       DateTimeOffset.UtcNow.AddDays(-1));
var snapshot = await query.QueryAsync(new QueryRequest(asYesterday), token);
```

This returns what Mneme knew at that instant (uses `created_at` /
`expired_at`). Useful for explaining historical decisions.

### 5c. Synthesized bundle

```csharp
var bundle = await query.DistillAsync(
    token.Workstream!.Value, new DistillOptions(), token);

Console.WriteLine(bundle.Orientation.Paragraph);
foreach (var section in bundle.Sections)
{
    Console.WriteLine($"## {section.Title}");
    Console.WriteLine(section.Content);
}
```

This calls the registered `IDistiller`. With no `IDistiller`
registered, a heuristic fallback produces a usable but un-LLM bundle —
ship that for early development, plug in a real distiller for
production.

```csharp
services.AddSingleton<IDistiller>(_ => new MyBundleSynth(myOtherChatClient));
```

---

## 6. Curating memory

Memory drifts. Users will ask Mneme to forget something, fix a wrong
claim, or pin an authoritative one. All of these are first-class
operations:

```csharp
var curator = sp.GetRequiredService<IMemoryCurator>();
var cap = new CurationCapability(
    Principal:  token.Principal,
    Workstream: token.Workstream!.Value,
    NotBefore:  DateTimeOffset.UtcNow,
    NotAfter:   DateTimeOffset.UtcNow.AddDays(30),
    CanAmend: true, CanAnnotate: true, CanPin: true, CanDemote: true,
    CanSplit: true, CanMerge: true, CanRevert: true, CanReview: true);

// Amend (race-safe via preStateHash):
var preState = PreStateHasher.ComputeHash(factory, new EventId(evtId));
await curator.AmendFactAsync(new FactId(factId), preState,
    new FactAmendment("v2 ships November 1, not October 15", "moved per release council"),
    cap);

// Annotate (non-destructive):
await curator.AnnotateAsync(new EventId(evtId), "Cross-check with #release channel.", cap);

// Pin (boost retrieval):
await curator.PinAsync(new EventId(evtId), PinScope.Workstream, multiplier: 2.5f, cap);

// Demote (suppress):
await curator.DemoteAsync(new EventId(evtId), multiplier: 0.3f, cap);

// Revert any prior curation:
await curator.RevertCurationAsync(curationEventId, "actually the original was right", cap);
```

Curations are themselves events in `curation_events`. Reverts add new
events; nothing is ever deleted.

To revoke an event for privacy / accuracy reasons (tombstone the body,
keep the metadata for audit):

```csharp
var rev = sp.GetRequiredService<IRevocationService>();
await rev.RevokeAsync(new EventId(evtId), token.Workstream!.Value, token.Principal,
    "user requested deletion under GDPR");
```

---

## 7. Wiring an MCP server

If your agent runtime speaks MCP, point it at the included server:

```pwsh
dotnet build src/Mneme.Mcp/Mneme.Mcp.csproj
```

Add to your MCP client config (Copilot CLI's `~/.copilot/mcp-config.json`,
Claude Desktop, Cursor, …):

```json
{
  "mcpServers": {
    "mneme": {
      "command": "C:\\path\\to\\src\\Mneme.Mcp\\bin\\Debug\\net8.0\\Mneme.Mcp.exe",
      "env": {
        "MNEME_WORKSTREAM_ID": "my-team-q3-2026",
        "MNEME_SQLITE_PATH":   "C:\\Users\\alice\\.mneme\\local.db",
        "MNEME_USER_ID":       "alice@contoso.com"
      }
    }
  }
}
```

The agent then has direct tool access to:

- `remember(content, source?)` — direct-ingest an Evidence event.
- `query(freeText?, asOf?, limit?, explain?)` — ranked retrieval.
- `list_recent(limit?)` — last-N events.
- `distill(forceRefresh?, tokenBudget?)` — synthesized bundle.
- `distill_session(sessionId, entries)` — run session distillation
  over a JSON array of `ContextEntry` objects.
- `get_watermark(sessionId)` — read the last-distilled entry id.
- `forget(eventId, reason)` — revoke an event.
- `improve(operation, targetId, …)` — curation operations.

---

## 8. Wiring an MAF agent

```csharp
using Mneme.Agents.AI;

services.AddMneme(o => { /* … */ });
services.AddMnemeContextProvider(new WorkstreamId("my-team-q3-2026"));
services.AddSingleton<IDistiller>(_ => new MyBundleSynth(modelClient));  // optional but recommended

// In your MAF agent setup:
var provider = sp.GetRequiredService<MnemeContextProvider>();
agent.ContextProviders.Add(provider);  // exact API depends on your MAF host shape
```

The provider's `InvokingAsync` runs before every agent turn, pulls the
current bundle, and prepends it as a single `ChatRole.System` message.
**The provider is read-only by design.** Capture flows through a
separate `DistillSessionAsync` call the host makes on its own schedule
(typically a `BackgroundService` worker).

A minimal capture worker:

```csharp
public sealed class SessionCaptureWorker : BackgroundService
{
    private readonly IMemoryAgent _agent;
    private readonly ISessionChatLog _chatLog;   // your own type
    private readonly CapabilityToken _token;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var session in _chatLog.ActiveSessions())
            {
                var watermark = await _agent.GetWatermarkAsync(session, ct);
                var newEntries = _chatLog.EntriesSince(session, watermark?.LastDistilledEntryId);
                if (newEntries.Count == 0) continue;
                await _agent.DistillSessionAsync(session, newEntries, _token, ct);
            }
            await Task.Delay(_interval, ct);
        }
    }
}
```

---

## 9. Running the sidecar

If you want Mneme as its own process (e.g., shared by several local
agents):

```pwsh
cd src/Mneme.Sidecar
$env:MNEME_WORKSTREAM_ID = "shared-ws"
$env:MNEME_SQLITE_PATH   = "C:\\mneme\\shared.db"
$env:MNEME_USER_ID       = "alice@contoso.com"
$env:MNEME_BEARER_TOKEN  = "<random-string>"
dotnet run
```

HTTP endpoints (bearer auth via `Authorization: Bearer <token>`):

- `GET  /healthz` — liveness.
- `GET  /readyz`  — readiness (DB reachable, schema initialized).
- `POST /api/v1/ingest`           — direct ingest.
- `POST /api/v1/distill_session`  — session distillation.
- `GET  /api/v1/watermark/{id}`   — read watermark.
- `POST /api/v1/query`            — ranked retrieval.
- `POST /api/v1/distill`          — synthesized bundle.
- `POST /api/v1/curate`           — curation operations.

A `Dockerfile` is included.

---

## 10. Cloud sync (optional)

```csharp
services.AddSingleton<ISyncStore>(_ => new FileSystemSyncStore("\\\\backups\\mneme"));
// (or your own S3 / Azure Blob implementation)

var sync = sp.GetRequiredService<SyncEngine>();
await sync.PushAsync(token.Workstream!.Value);   // snapshot batch up
await sync.PullAsync(token.Workstream!.Value);   // merge in remote batches
```

Snapshots are gzipped JSONL, append-only. Merge is
`INSERT OR IGNORE ON event_id` — no conflict resolution needed.

---

## 11. Capability scoping

Replace the default `CapabilityToken` for finer-grained read control:

```csharp
services.AddSingleton(_ => new CapabilityToken(
    Principal:          new PrincipalId("bob@contoso.com"),
    Workstream:         new WorkstreamId("my-team-q3-2026"),
    NotBefore:          DateTimeOffset.UtcNow,
    NotAfter:           DateTimeOffset.UtcNow.AddHours(8),
    AllowedCategories:  new[] { EpistemicCategory.Fact, EpistemicCategory.Decision },
    CrossWorkstream:    false,
    IncludeTechnical:   false));
```

The query API will reject (404-style empty result) anything the token
doesn't allow.

---

## 12. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `InvalidOperationException: DistillSessionAsync requires an ISessionDistiller` | No host distiller registered. | `services.AddSingleton<ISessionDistiller>(...)`. |
| `result.WasNoOp == true` but you expected new events | Same `(session, from, to)` was distilled before. | Pass entries strictly after `GetWatermarkAsync(session).LastDistilledEntryId`. |
| MCP server fails to start | Missing env vars. | Set `MNEME_WORKSTREAM_ID`, `MNEME_SQLITE_PATH`, `MNEME_USER_ID`. |
| `cannot reach memory_events` | First-run schema not initialised. | Confirm the SQLite path is writable; `AddMneme` calls `SqliteSchema.Initialize` on startup. |
| `Mneme.dll` locked during rebuild | An `Mneme.Mcp.exe` instance is still running (Copilot CLI may have launched one). | `Get-Process Mneme.Mcp \| Stop-Process -Id $_.Id -Force` then rebuild. |
| Distillation returns empty bundles | No `IDistiller` registered; falling back to the heuristic. | Either register an `IDistiller` or accept the heuristic for early dev. |
| Test recall numbers look bad | Phase 4.5 LoCoMo baseline is honest about being 1/6 without vector search. | Phase 11 (sqlite-vec) is the natural next lever. |

---

## 13. Going deeper

- [README.md](README.md) — project pitch + status.
- [ARCHITECTURE.md](ARCHITECTURE.md) — design walkthrough.
- [AGENTS.md](AGENTS.md) — locked decisions for AI contributors.
- [plans/plan.md](plans/plan.md) — long-form design.
- [samples/Mneme.Samples.AgentHost/README.md](samples/Mneme.Samples.AgentHost/README.md)
  — the canonical end-to-end sample (runs offline with a stub LLM).
