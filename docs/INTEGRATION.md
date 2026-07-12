# Integrating Mneme — one-shot recipe

A self-contained guide for adding Mneme to a .NET host. Written to be
followed top-to-bottom (by a human or an AI coding agent) to produce a
working integration in one pass. For the full reference, see
[USAGE.md](../USAGE.md).

## TL;DR

1. `dotnet add package Mneme --prerelease`
2. `services.AddMneme(o => { o.WorkstreamId = ...; o.SqlitePath = ...; o.UserId = ...; })`
3. Implement **one** interface — `ISessionDistiller` (chat turns → memory events).
4. Call `agent.DistillSessionAsync(session, newEntries, token)` as a session grows.
5. Call `query.QueryAsync(...)` to read memory back, capability-checked.

## The mental model (don't skip)

**The host owns the chat log; Mneme owns the interpretation.** Mneme never
stores raw chat turns. You periodically hand it the entries that accumulated
since its last *watermark* for a session; it runs *your* distiller, ingests
the resulting epistemic events (Facts, Decisions, …) with citations back to
the source entries, and advances the watermark atomically. Re-distilling is
just calling again with a lower watermark.

## Step 1 — install

```pwsh
dotnet add package Mneme --prerelease
```

## Step 2 — register services

```csharp
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;

var services = new ServiceCollection();

services.AddMneme(o =>
{
    o.WorkstreamId = "acme-q3-2026";                                  // isolation boundary
    o.SqlitePath   = Path.Combine(AppContext.BaseDirectory, "mneme.db");
    o.UserId       = "alice@acme.com";
});

// The ONE thing you must implement (Step 3). Any Microsoft.Extensions.AI
// IChatClient — OpenAI, Azure, Anthropic, Ollama, local — works.
services.AddSingleton<ISessionDistiller>(sp => new ChatSessionDistiller(myChatClient));

await using var sp = services.BuildServiceProvider();
var agent = sp.GetRequiredService<IMemoryAgent>();
var query = sp.GetRequiredService<IMemoryQueryAPI>();
var token = sp.GetRequiredService<CapabilityToken>();
```

`AddMneme` wires storage, schema init, redactor, classifier, the projector
pipeline, entity resolution, and a default `CapabilityToken` scoped to your
`UserId` + `WorkstreamId`.

## Step 3 — implement `ISessionDistiller`

This is where *your* model turns conversation into durable memory. Minimal,
correct skeleton:

```csharp
using Mneme.Contracts;

public sealed class ChatSessionDistiller : ISessionDistiller
{
    private readonly IMyChat _chat;   // your LLM wrapper
    public ChatSessionDistiller(IMyChat chat) => _chat = chat;

    public string Id => "my-app/session-distiller/v1";

    public async Task<SessionDistillationResult> DistillAsync(
        SessionDistillationRequest req, CancellationToken ct = default)
    {
        // req.Entries are the turns since the watermark, in order.
        // Ask your model for atomic, self-contained facts (resolve pronouns
        // to names; attribute to the speaker). Return JSON, then map it.
        var facts = await _chat.ExtractFactsAsync(req.Entries, ct);

        var events = facts.Select(f => new DistilledEvent(
            Payload: new FactPayload(f.Statement, Array.Empty<EventId>()),
            // cite the source entry ids so Mneme can stamp a SessionRange:
            SupportingEntryIds: f.FromEntryIds));

        return new SessionDistillationResult(events.ToList());
    }
}
```

Notes:
- Return an **empty** `Events` list when a slice has nothing durable — that's
  valid and the watermark still advances.
- `req.PriorFacts` gives you a few already-known facts so you can avoid
  re-extracting or can supersede them.
- Emit **multiple** small facts over one vague sentence — it improves recall.
- Never put secrets in payloads; Mneme redacts inline, but keep them out.

## Step 4 — feed sessions in

```csharp
// `session` identifies the conversation; `entries` are everything the host
// accumulated (chat turns, tool outputs, file reads). Mneme filters to the
// tail past its watermark for you.
var entries = host.GetContextEntries(session);   // IReadOnlyList<ContextEntry>
await agent.DistillSessionAsync(session, entries, token, ct);
```

A `ContextEntry` is `(EntryId, Timestamp, Kind, Text, SourceRef)` — e.g.
`new ContextEntry("t42", DateTimeOffset.UtcNow, ContextEntryKind.UserMessage,
"Alice: let's ship auth on Friday", "session-7")`.

## Step 5 — read memory back

```csharp
// Ranked, capability-checked, single-workstream by default.
var result = await query.QueryAsync(new QueryRequest(
    new QuerySpec(new WorkstreamId("acme-q3-2026"),
        FreeText: "what did we decide about the auth rollout?",
        Limit: 25)), token, ct);

foreach (var item in result.Items)
    Console.WriteLine($"[{item.ValidAt:yyyy-MM-dd}] {item.Summary}");

// Point-in-time: what did we know as of a past instant?
var asOf = await query.QueryAsync(new QueryRequest(
    new QuerySpec(ws, FreeText: "auth rollout", AsOf: someDate)), token, ct);

// Synthesized bundle for injecting into a prompt (uses your IDistiller if wired):
var bundle = await query.DistillAsync(new QuerySpec(ws, FreeText: "auth"), token, ct);
```

## Common pitfalls

- **"No ISessionDistiller registered."** You called `DistillSessionAsync`
  without registering one. Add the `services.AddSingleton<ISessionDistiller>`
  line. (Direct `agent.IngestAsync` of pre-shaped events needs no distiller.)
- **Cross-workstream query returns nothing.** Workstream isolation is on by
  default; querying a different `WorkstreamId` than your token grants yields
  nothing unless the capability explicitly allows it.
- **Semantic search inactive.** Register an `IEmbeddingProvider` to enable the
  hybrid semantic+BM25 path; without one, retrieval is lexical (BM25) only.
- **Don't commit the `.db`.** Add `*.mneme.db*` (or your db name) to
  `.gitignore`.

## Optional integrations

- **Microsoft Agent Framework:** `dotnet add package Mneme.Agents.AI` →
  register `MnemeContextProvider` so a MAF agent hydrates its prior context
  from Mneme. See USAGE.md §8.
- **MCP server:** `dotnet tool install -g Mneme.Mcp` → point any MCP client at
  the `mneme-mcp` command. See USAGE.md §7.

That's the whole integration. Reference: [USAGE.md](../USAGE.md) ·
[ARCHITECTURE.md](../ARCHITECTURE.md).
