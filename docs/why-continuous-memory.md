# Why Continuous Agent Memory Matters

> A primer on agent memory: why it matters, the core types, how memory
> updates, the operations a memory layer must support, and the tools and
> frameworks shipping today.

Historically, AI has operated like Dory from *Finding Nemo*, resetting
entirely with each new prompt or thread. Large language models are
**stateless** by default — the model weights don't change between calls,
and anything not in the current context window is simply gone. Continuous
memory agents resolve this by bolting on dedicated, persistent layers
that survive across turns, sessions, and projects.

The payoff:

- **Eliminate Context Drift:** Prevent models from stuffing vast, noisy
  conversation histories into the context window. Retrieve only what's
  relevant instead of replaying everything.
- **Reduce Hallucination:** Rely on an accumulated, factual baseline of
  the user's specific world rather than guessing.
- **Stop Repeating Mistakes:** Store past failures and successes,
  adjusting future logic and workflows based on real experience.
- **Personalize Over Time:** Build a durable model of the user's
  preferences, vocabulary, and goals so the agent feels like a
  long-term collaborator rather than a stranger every session.
- **Control Cost and Latency:** A focused, retrieved memory beats a giant
  context window on both token spend and time-to-first-token — and often
  on accuracy, since long contexts suffer from "lost in the middle"
  degradation.

> **Memory is not the context window.** The context window is short-term
> working state that vanishes when the conversation ends. Memory is the
> system that decides what is worth keeping, stores it durably, and
> surfaces the right slice back into the window when it's needed.

---

## The Memory Hierarchy: Working vs. Long-Term

A useful first cut borrows the human distinction between fast, volatile
working memory and slower, durable long-term memory.

| | Working / Short-Term | Long-Term |
|---|---|---|
| **Lives in** | The context window | An external store (DB, vector index, knowledge graph, files) |
| **Lifespan** | The current turn / session | Across sessions, indefinitely |
| **Capacity** | Bounded by the model's context limit | Effectively unbounded |
| **Speed** | Instant (already in context) | Requires a retrieval step |
| **Cost** | Tokens every call | Storage + a retrieval call |

The job of a memory system is to **move the right information between
these tiers at the right time** — promoting durable facts out of the
window before they're evicted, and retrieving them back when relevant.

---

## The 4 Core Types of Memory

Advanced agent architectures increasingly map memory to human psychology
to provide robust, multi-dimensional intelligence. This mapping is
formalized in the [CoALA paper (Sumers, Yao, Narasimhan, Griffiths,
2024)](https://arxiv.org/abs/2309.02427), now the most widely cited
framework for reasoning about agent memory.

### 1. Short-Term / Working Memory

Active context currently being processed, typically limited to the
model's immediate context window. Holds the running conversation, the
current task state, intermediate reasoning, and recently retrieved
facts. It is fast but finite — and the primary thing a long-term memory
system exists to protect from overflow.

### 2. Semantic Memory

The agent's long-term store of **factual knowledge** about the world and
the user — names, preferences, relationships, domain facts. It is
frequently organized into **Knowledge Graphs** (entities + typed edges)
so the agent can track causal and relational links, not just isolated
strings.

> *Example:* "Alice prefers PostgreSQL over MySQL" and "Alice leads the
> payments team" are semantic facts that can be retrieved and combined.

### 3. Episodic Memory

Personal experiences and **event sequences** — what happened, when, and
in what order. Where semantic memory stores *what is true*, episodic
memory stores *what occurred*.

> *Example:* remembering a previous task result, a past user correction,
> or how a similar problem was solved last month, then applying it to a
> new but related interaction. Episodic memory is what enables few-shot
> learning from the agent's own history.

### 4. Procedural Memory

The **"how-to" memory** — the cached routines, tool workflows, and skills
an agent uses to get things done. As agents learn through interactions,
frequently used procedures get distilled and cached, allowing faster,
more reliable task execution without re-deriving the approach each time.

> *Example:* an agent that has learned the exact sequence of tool calls to
> deploy a service stores that as a reusable procedure rather than
> re-planning it from scratch. In practice this often manifests as the
> agent refining its own system prompt or a library of saved workflows.

---

## How Memory Updates

Agents write to and refine their memory stores through different
architectural approaches, ranging from real-time to fully offline.
Choosing among them is largely a **latency vs. quality trade-off**.

### Explicit (Hot Path)

The agent actively calls a memory tool (e.g., `add_memory`,
`update_memory`) directly while processing user input. Writes happen
**synchronously** in the request loop.

- ✅ Immediate — the next turn sees the new memory.
- ✅ Transparent and easy to debug.
- ❌ Adds latency and token cost to every interaction.
- ❌ Relies on the model remembering to call the tool.

### Implicit (Background)

Background processes analyze interactions **asynchronously** to distill
insights, avoiding any added latency on the live user interaction. The
agent responds first; a separate worker decides what to remember after.

- ✅ Zero added latency on the hot path.
- ✅ Can use a cheaper / different model for extraction.
- ❌ Eventually-consistent — a fact may not be queryable for seconds.
- ❌ More moving parts (queues, workers).

### Reflection & Compression

Specialized controller mechanisms periodically analyze logs or full
interaction trajectories, condensing unstructured data into higher-level,
compressed facts or rules. This is where raw episodes become durable
semantic and procedural memory.

- ✅ Produces the highest-quality, most useful memory.
- ✅ Naturally deduplicates and resolves contradictions.
- ❌ Most expensive; usually run on a schedule or at session boundaries.

> Many production systems combine all three: explicit tools for
> user-asserted facts, background distillation for the bulk of capture,
> and scheduled reflection to compress and reconcile.

---

## The Core Operations a Memory Layer Must Support

Beyond storing, a serious memory system has to manage the **lifecycle**
of what it knows. The hard problems are rarely the writes — they're
everything after.

- **Retrieval / Ranking:** Surface the most relevant memories for the
  current context. Usually a blend of semantic similarity (embeddings),
  keyword search, recency, and importance weighting.
- **Consolidation:** Merge related or duplicate memories into a single
  canonical record so the store doesn't bloat with near-duplicates.
- **Conflict Resolution & Updating:** When new information contradicts an
  old fact ("Alice moved from payments to platform"), decide whether to
  supersede, version, or branch — ideally without losing the history.
- **Forgetting / Decay:** Down-weight or expire stale, low-value
  memories. Unbounded memory is as useless as no memory; relevance must
  degrade over time unless reinforced.
- **Temporal Awareness:** Track *when* a fact was true versus *when it
  was recorded* (bi-temporality). This is what lets an agent answer "what
  did we believe last quarter?" correctly.
- **Provenance & Audit:** Know *why* a memory exists and *where it came
  from*, so a wrong memory can be traced and corrected.
- **Curation (Human-in-the-Loop):** Let a human pin authoritative facts,
  demote noise, amend errors, and revoke sensitive data.

---

## Memory Scopes

A memory isn't globally true — it's true *for someone* in *some context*.
Most mature systems support multiple scopes:

- **User scope:** Durable facts about an individual that persist across
  every session and agent (preferences, identity, history).
- **Session scope:** Context relevant only within a single conversation
  or task run.
- **Agent scope:** Knowledge shared by a particular agent across all its
  users (learned procedures, domain expertise).
- **Workstream / Organization scope:** Memory shared across a team or
  project, with isolation boundaries so workstreams don't leak into each
  other.

Getting scope isolation right is also a **security and privacy**
requirement, not just an organizational nicety.

---

## Top Tools and Frameworks

Developers and teams are actively building these concepts into production
using specialized open-source and dedicated memory layers:

- **[Mem0](https://github.com/mem0ai/mem0):** A highly popular dedicated
  memory layer built specifically for AI, providing multi-level memory
  scopes (user, session, agent) and built-in lifecycle management. Known
  for a simple `add` / `search` API and strong showings on long-
  conversation benchmarks.
- **[Letta (formerly MemGPT)](https://github.com/letta-ai/letta):** An
  orchestration framework that lets LLMs manage their own memory
  hierarchy, creating stateful agents that operate like an operating
  system paging memory in and out — the agent decides what to keep in
  "core" memory versus what to archive.
- **[Zep / Graphiti](https://github.com/getzep/graphiti):** A temporal
  **knowledge-graph** memory layer. Graphiti builds a bi-temporal graph
  of entities and relationships from conversation, tracking how facts
  change over time — strong where causal and relational reasoning matter.
- **[memU](https://github.com/NevaMind-AI/memU):** A filesystem-inspired
  memory layer that continuously learns and evolves dialogs and behaviors
  into structured memory files.
- **[LangGraph / LangMem](https://www.langchain.com/blog/memory-for-agents):**
  LangChain's low-level memory store, designed to give developers
  fine-grained control over how memory is gathered, shaped, and
  retrieved within a graph-structured agent.
- **[Cognee](https://github.com/topoteretes/cognee):** An ECL (Extract,
  Cognify, Load) pipeline that turns raw data into a queryable
  knowledge + vector store for agents.

> No single tool wins every workload. Vector-first layers (Mem0) are
> simplest for personalization; graph-first layers (Zep/Graphiti) win on
> temporal and relational reasoning; OS-style frameworks (Letta) win when
> the agent should manage its own memory budget.

---

## Where Mneme Fits

[Mneme](README.md) is a local-first, .NET-native take on this problem
space. It is a **bi-temporal, append-only event log** of seven epistemic
categories (Evidence, Fact, Decision, Hypothesis, Goal, Action, Outcome)
on SQLite, with a deliberate architectural stance:

- **The host owns the chat log; Mneme owns the interpretation.** Mneme
  never duplicates raw conversation — the host periodically hands it the
  new entries since a per-session *watermark*, and a host-supplied
  distiller turns them into structured epistemic events. This is the
  **implicit / background** and **reflection / compression** update
  models combined, with the LLM fully pluggable.
- **Bi-temporality is first-class**, directly addressing the temporal-
  awareness requirement above.
- **Human-in-the-loop curation** (`amend` / `annotate` / `pin` /
  `demote` / `revert`) is a built-in surface, not an afterthought.
- **Capability-checked, workstream-scoped** retrieval enforces memory
  scope isolation at the API layer.

See [ARCHITECTURE.md](ARCHITECTURE.md) for how these map to the concepts
above, and the [memory-systems primer](plans/memory-systems-primer.md)
for a deeper field survey.

---

## Further Reading

- [CoALA: Cognitive Architectures for Language Agents](https://arxiv.org/abs/2309.02427)
  — the foundational mapping of human memory types to agents.
- [LangChain — Memory for Agents](https://www.langchain.com/blog/memory-for-agents)
  — practical, application-specific framing.
- [Graphiti / Zep](https://github.com/getzep/graphiti) — temporal
  knowledge-graph memory in practice.
- Mneme's own [`plans/research-existing-systems.md`](plans/research-existing-systems.md)
  — a survey of 19 memory systems and the build-vs-adopt analysis.
