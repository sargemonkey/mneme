# Mneme.Mcp

A [Model Context Protocol](https://modelcontextprotocol.io/) server that
exposes Mneme's query API as MCP tools. Lets any MCP-compatible agent
(Copilot, Claude, Cursor, custom ACP clients) read from a Mneme instance
without writing .NET binding code.

## Status

**Not started.** Lands in Phase 8 of [`plans/plan.md`](../../plans/plan.md).

## Planned tools (preview)

| Tool | Purpose |
|---|---|
| `mneme_query` | Capability-checked query over workstream memory |
| `mneme_distill` | Returns a compressed context bundle for a workstream |
| `mneme_decision_history` | Walks the supersession chain for a Decision |
| `mneme_entity_lookup` | Resolves a referenced entity (with bi-temporal point-in-time option) |

## Planned dependencies

- `ModelContextProtocol` C# SDK (Apache 2.0) — the official MCP server library

## Design rules

- **Capability tokens are mandatory.** The MCP transport translates the
  caller's identity to a token; no token, no access.
- **No write tools in v1.** Ingest is the cockpit's job. v2 may add
  human-anchor / annotation tools, gated behind explicit human action.
