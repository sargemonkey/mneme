# Mneme.Agents.AI

Drop-in Microsoft Agent Framework integration for Mneme.

## Five-line setup

```csharp
using Mneme.Agents.AI;
using Mneme.Contracts;
using Mneme.Hosting;

services.AddMneme(o => { o.WorkstreamId = "my-agent"; o.SqlitePath = "..."; o.UserId = "..."; });
services.AddMnemeContextProvider(new WorkstreamId("my-agent"));   // <-- the integration
// Wire LLMs as needed:
// services.AddSingleton<ISessionDistiller>(_ => new MySessionDistiller(...));  // chat → events
// services.AddSingleton<IDistiller>(_         => new MyBundleSynthesizer(...));// events → bundle
```

Then in your MAF agent runtime:

```csharp
var provider = sp.GetRequiredService<MnemeContextProvider>();
agent.ContextProviders.Add(provider);   // exact API depends on your MAF host
```

## What it does

* **Before each agent call** (`InvokingAsync`): pulls the latest Mneme
  `ContextBundle` for the workstream and surfaces it as a single
  `ChatMessage(ChatRole.System, ...)` rendered as Markdown.

That's it on the MAF side. The provider is **read-only by design**.

## Where capture happens

Capture (chat → epistemic events) does **not** flow through the MAF
provider. The host owns the chat log and calls
`IMemoryAgent.DistillSessionAsync(...)` on its own schedule — typically
a background worker that periodically hands Mneme the entries that have
accumulated in the session since the last watermark. This keeps the
"host owns the chat log; Mneme stores only the interpretation"
invariant intact (an `InvokedAsync` per-turn pump would quietly
duplicate turns into the event log on every call).

See `samples/Mneme.Samples.AgentHost` for the canonical pattern.

## Where the LLM lives

Mneme has zero LLM dependency in this package too. Both the
session-distillation LLM and the bundle-synthesis LLM are host-supplied
implementations of `ISessionDistiller` and `IDistiller` respectively.

## Versions

Built against `Microsoft.Agents.AI.Abstractions` 1.0.0-preview.
Will need a bump when the package goes GA.
