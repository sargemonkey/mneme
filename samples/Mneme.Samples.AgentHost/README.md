# Sample: agent host that captures + distills via two LLMs

End-to-end illustration of how an agentic framework wires Mneme:

1. Framework hands every turn to Mneme via
   `CaptureSession.ProcessTurnAsync(turn, workstream)`.
2. Host's `ICapturePolicy` asks an LLM "is this turn worth remembering?"
   and returns 0+ `CaptureCandidate`s.
3. Mneme ingests each candidate through the same redaction / classification
   / append-only pipeline as any other ingest path.
4. Whenever the agent needs compact context, it calls
   `IMemoryQueryAPI.DistillAsync(workstream)`.
5. Mneme assembles a curation-aware `DistillationRequest` and hands it to
   the host's `IDistiller`, which calls a (possibly different) LLM.
6. SDK caches the resulting `ContextBundle` until the next ingest or
   curation invalidates it.

## Two LLMs, two jobs, host's choice

```csharp
// Could be the same client, different clients, different providers entirely:
IChatClient captureChat = new OpenAIChatClient(apiKey, "gpt-4o-mini");      // cheap
IChatClient distillChat = new AnthropicChatClient(apiKey, "claude-sonnet"); // heavier

services.AddSingleton<ICapturePolicy>(sp => new LlmCapturePolicy(captureChat));
services.AddSingleton<IDistiller>(sp     => new LlmDistiller(distillChat));
services.AddSingleton<ICaptureFilter>(sp =>
    new RecentDuplicateFilter(sp.GetRequiredService<SqliteConnectionFactory>()));
```

Mneme has zero LLM dependency. The host owns the model and the keys.

## Run it

```pwsh
cd samples/Mneme.Samples.AgentHost
dotnet run
```

A `StubChatClient` ships in the file so the sample runs offline. Replace
it with your real `IChatClient` and the rest of the host code is unchanged.
