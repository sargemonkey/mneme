# Sample: agent host that distills sessions periodically

End-to-end illustration of how a host wires Mneme when **the host owns
the chat history** and Mneme stores only the distilled interpretation
of it.

1. The host has full chat history for the session in its own store. Mneme
   never sees raw turns.
2. On a schedule (or at session-end, or whenever the host wants a fresh
   distillation), it calls
   `IMemoryAgent.DistillSessionAsync(sessionId, entries, capability)`
   passing the entries that have accumulated.
3. Mneme reads the persisted **watermark** for the session, filters the
   entries to the strict tail, and hands them to the host's
   `ISessionDistiller`.
4. The distiller LLM decides which entries become epistemic events
   (Fact / Decision / Goal / Hypothesis / Action / Outcome) and which to
   drop. Output events cite the supporting entry-id range.
5. Mneme ingests each event with a `Citation.SessionRange` provenance,
   then atomically advances the watermark.
6. Replays (same `(session, from, to)`) are idempotent no-ops.

When the agent needs compact prior context for its next turn, it calls
`IMemoryQueryAPI.DistillAsync(workstream)` — the read-side bundle
synthesizer, a separate concern with its own `IDistiller`.

## Two distillers, two jobs, host's choice

```csharp
// Ingest side: chat → epistemic events. Cheap model is fine.
IChatClient sessionChat = new OpenAIChatClient(apiKey, "gpt-4o-mini");
// Read side: events → orientation bundle. Pick something with reach.
IChatClient bundleChat  = new AnthropicChatClient(apiKey, "claude-sonnet");

services.AddSingleton<ISessionDistiller>(_ => new LlmSessionDistiller(sessionChat));
services.AddSingleton<IDistiller>(_         => new LlmBundleSynthesizer(bundleChat));
```

Mneme has zero LLM dependency. The host owns both models and both keys.

## Run it

```pwsh
cd samples/Mneme.Samples.AgentHost
dotnet run
```

`StubChatClient` ships in the file so the sample runs offline. Replace
each with your real `IChatClient` and the rest of the host code is
unchanged. The sample shows three distillation calls in sequence:

1. **First call**: watermark is null; all 5 entries eligible; distiller
   keeps decisions/goals, drops small talk.
2. **Replay**: same entries → idempotent no-op, watermark unchanged.
3. **Session grows**: only the new tail (2 entries) is processed.

Each call returns `(NewEvents, NewWatermark, Dropped, WasNoOp)`.
