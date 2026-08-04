# Mneme.Studio.Agent

A **Photino + Blazor Server desktop app** that connects to a coding agent over
the [Agent Client Protocol (ACP)](https://agentclientprotocol.com) using
[`LibAcp`](https://www.nuget.org/packages/LibAcp), and uses **Mneme** to distill
the conversation into epistemic memory as you talk.

It's a working demonstration of Mneme's locked design decision —
*"the host owns the chat log; Mneme owns the interpretation"* — because an **ACP
client is exactly a Mneme host**:

| ACP role | Mneme role |
|---|---|
| **Client** owns the conversation, drives the agent, holds the context buffer | **Host** owns the chat log; Mneme never stores raw turns |
| **Agent** does the work, streams back messages | (external) |

The app plays the ACP **client** and the Mneme **host** simultaneously. The two
libraries compose cleanly precisely because they draw the same boundary.

## The loop

```
you type ─▶ ContextEntry(UserMessage) ─┐
                                        ├─▶ append to host-owned buffer
agent reply (ACP session/update) ──────┘        │
   ▲                                             ▼
   │                          IMemoryAgent.DistillSessionAsync(session, buffer, token)
   └── PromptAsync (ACP) ──────────────┐         │  runs the host ISessionDistiller,
                                       │         │  ingests epistemic events with a
   ACP agent subprocess / in-proc mock ┘         │  Citation.SessionRange stamp, and
                                                 ▼  advances the per-session watermark
                              IMemoryQueryAPI.ListRecentAsync ─▶ "Distilled memory" panel
```

## Run it

```pwsh
cd src/Mneme.Studio.Agent
dotnet run                # opens the native window
dotnet run -- --smoke     # headless end-to-end check (no window), prints distilled memory
```

No API key required. The distillation LLM is **real GitHub Copilot**, driven over
its native ACP server (`copilot --acp`) — the same transport MuxiMuxi uses:

- **Distiller LLM = GitHub Copilot (via `copilot --acp`).** Mneme's own
  `ISessionDistiller` / `IDistiller` logic runs the extraction prompt; Copilot
  does the interpretation. Turns become **atomic, self-contained epistemic
  facts** (pronouns resolved, speaker-attributed, category-tagged) — not verbatim
  capture. We never store Copilot's conversational output as memory; we run
  Mneme's distillation *over the turn-based conversation* with Copilot as the LLM.
  Requires the `copilot` CLI on PATH and logged in (`copilot login`).
- **Conversation agent** (interactive chat only): also real Copilot over ACP,
  started lazily on the first chat message. Corpus replay doesn't need it.
- **Offline fallback:** set `MNEME_AGENT=mock`, or if the `copilot` CLI is
  unavailable the app degrades to an in-process mock agent + the deterministic
  `HeuristicSessionDistiller` (keyword-based, no extraction) so it still runs.

> **Latency:** Copilot has a ~30–50s one-time ACP cold start (warmed up in the
> background at launch), then ~4–8s per distillation call. The corpus bar shows
> a **⏳ distilling…** indicator, and auto-play waits for each turn to finish.

## Watch memory form over a corpus

The window has a **corpus replay** bar. Pick a bundled
[LoCoMo](https://github.com/snap-research/locomo)-shaped conversation, **Load**
it, then **Step** through it turn-by-turn or **Auto**-play at a chosen speed.
Each replayed turn is fed into `IMemoryAgent.DistillSessionAsync`, Copilot
extracts the durable facts, and the **Distilled memory** panel fills in live.

To replay the **real** LoCoMo dataset instead of the bundled sample, set
`MNEME_LOCOMO_PATH` to a full LoCoMo JSON file before launching.

- **Reject a memory:** every item in the memory panel has a ✕ — clicking it calls
  `IRevocationService.RevokeAsync`. Mneme is append-only, so this records a
  revocation tombstone (who/when/why) rather than deleting; the query API then
  filters the event out.
- **Sleep mode (consolidation):** the **😴 Sleep** button runs the read-side
  distiller (`IMemoryQueryAPI.DistillAsync`, also Copilot-backed) over everything
  captured and shows the **condensed** `ContextBundle` — a one-paragraph
  orientation plus per-category sections. This is the compressed synthesis a
  consuming agent loads instead of the raw event dump.

## Layout

| File | Role |
|---|---|
| `Program.cs` | Photino window over an in-process Blazor Server host (+ `--smoke`, `--copilot-smoke`) |
| `Acp/AcpAgentConnection.cs` | ACP link — real `copilot --acp` subprocess **or** the in-process mock |
| `Acp/StudioAcpClient.cs` | The ACP `IClient` — collects streamed agent text, auto-answers permissions |
| `Acp/MockAcpAgent.cs` | The bundled offline ACP `IAgent` (fallback) |
| `Memory/CopilotChatCompletion.cs` | Copilot-over-ACP as a chat-completion backend for the distillers |
| `Memory/LlmSessionDistiller.cs` | Mneme capture distiller — LLM extraction of atomic facts |
| `Memory/LlmBundleDistiller.cs` | Mneme read-side distiller — LLM sleep/consolidation prose |
| `Memory/HeuristicSessionDistiller.cs` | Offline fallback distiller (no LLM) |
| `Corpus/CorpusLoader.cs` | Self-contained LoCoMo-shape loader (no ONNX benchmark dep) |
| `Corpus/locomo-sample.json` | Bundled sample corpus (copied to `corpus/` in output) |
| `Memory/AgentChatService.cs` | Orchestrates chat + corpus feed + reject + sleep |
| `Components/Pages/ChatPage.razor` | Conversation + corpus bar + memory panel + sleep overlay |

The SQLite store lives under `bin/.../data/mneme.studio.agent.db` (workstream
`studio-agent`). Point `Mneme.Studio.Electron` or `Mneme.Studio.Desktop` at it to
browse / curate what got distilled.
