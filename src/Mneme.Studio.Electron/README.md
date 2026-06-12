# Mneme.Studio.Electron

Live desktop Studio for Mneme. Pure Electron — **no HTTP server**:

- Reads SQLite directly via `better-sqlite3` (read-only WAL connection).
- Polls the db mtime + row-count once per second for live updates.
- Mutations shell out to the C# `Mneme.Cli` so curation / revocation /
  amend logic stays in canonical .NET (preserves the stale-state guard,
  append-only invariants, capability checks).

## First-run setup

```pwsh
cd src/Mneme.Studio.Electron
npm install                                      # installs electron + better-sqlite3
npx electron-rebuild -f -w better-sqlite3        # rebuilds the native binding for Electron's Node
npm start
```

## Config

Defaults to the same database the MCP server writes to
(`%USERPROFILE%\.mneme\copilot.db`, workstream `copilot-cli`).
Override via env:

| var | default |
|---|---|
| `MNEME_SQLITE_PATH` | `~/.mneme/copilot.db` |
| `MNEME_WORKSTREAM_ID` | `copilot-cli` |
| `MNEME_USER_ID` | OS username |
| `MNEME_CLI_PATH` | `..\..\Mneme.Cli\bin\Debug\net8.0\Mneme.Cli.exe` |

You can also switch the database from inside the app (📂 db button) and
the workstream via the dropdown.

## What you can do

| UI | Backed by |
|---|---|
| Live event timeline (newest first), color-coded category + classification badges, expandable raw payload | Direct SQLite read |
| **Revoke** — tombstone an event (audit trail preserved) | `Mneme.Cli revoke` |
| **Annotate** — attach human commentary | `Mneme.Cli annotate` |
| **Pin / Demote** — multiplier override | `Mneme.Cli pin/demote` |
| **Amend** — replace content with stale-state guard | `Mneme.Cli amend` |
| **Revert** — undo any curation in the log | `Mneme.Cli revert` |
| Curation log panel (right) with per-entry revert | Direct SQLite read |
| Workstream picker + database picker | Local IPC |

## Architecture

```
+--------------------+     poll mtime+rows     +--------------+
| renderer (HTML/JS) | <---------------------- | main.js (IPC)|
|  - timeline        |                         |   |          |
|  - curation log    |                         |   v          |
|  - action buttons  | --- IPC: cli(args) ---> | spawn(...)   |
+--------------------+                         |   |          |
                                               |   v          |
                                               | Mneme.Cli.exe|
                                               |  -> SQLite   |
                                               +--------------+
```

No HTTP server. No Blazor. No browser tab. Just a native window over a
WebView wrapping local files, talking SQLite directly + shelling out to
a small C# binary for writes.
