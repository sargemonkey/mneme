# Mneme.Agents.AI

Drop-in Microsoft Agent Framework integration for Mneme.

## Five-line setup

```csharp
using Mneme.Agents.AI;
using Mneme.Contracts;
using Mneme.Hosting;

services.AddMneme(o => { o.WorkstreamId = "my-agent"; o.SqlitePath = "..."; o.UserId = "..."; });
services.AddMnemeContextProvider(new WorkstreamId("my-agent"));   // <-- the integration
// Optionally: services.AddSingleton<ICapturePolicy>(sp => new MyPolicy(...));
//             services.AddSingleton<IDistiller>(sp => new MyDistiller(...));
```

Then in your MAF agent runtime:

```csharp
var provider = sp.GetRequiredService<MnemeContextProvider>();
agent.ContextProviders.Add(provider);   // exact API depends on your MAF host
```

## What it does

* **Before each agent call** (`InvokingAsync`): pulls the latest
  Mneme `ContextBundle` for the workstream and surfaces it as a
  single `ChatMessage(ChatRole.System, ...)` rendered as Markdown.
* **After each agent call** (`InvokedAsync`): if an `ICapturePolicy`
  is registered, pumps request + response messages through the
  capture pipeline so the next turn sees the most recent state.

## Where the LLM lives

Mneme has zero LLM dependency in this package too. The distillation
LLM comes from whatever `IDistiller` the host registers — same
contract as everywhere else in Mneme.

## Versions

Built against `Microsoft.Agents.AI.Abstractions` 1.0.0-preview.
Will need a bump when the package goes GA.
