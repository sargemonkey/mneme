# Publishing Mneme to NuGet

This is the release runbook for the Mneme NuGet packages. Releases are
**tag-driven**: pushing a `v*` tag builds, tests, packs, pushes to nuget.org,
and creates a GitHub Release. See
[`.github/workflows/release.yml`](../.github/workflows/release.yml).

## Packages produced

| Package | Kind | What it is |
|---|---|---|
| `Mneme.Contracts` | library | BCL-only interfaces + DTOs (the allowlist-safe contract surface). |
| `Mneme` | library | The memory substrate implementation (SQLite-backed). Depends on `Mneme.Contracts`. |
| `Mneme.Agents.AI` | library | Microsoft Agent Framework integration (`MnemeContextProvider`). |
| `Mneme.Mcp` | **.NET tool** | MCP server, installable via `dotnet tool install -g Mneme.Mcp` → `mneme-mcp`. |

`Mneme.Cli`, `Mneme.Sidecar`, `Mneme.Studio*`, tests, benchmarks, and samples are
intentionally **not** packable.

Each package embeds `README.md` (rendered on nuget.org), `LICENSE` (Apache-2.0),
`NOTICE`, XML docs, and a symbol package (`.snupkg`). Source Link is enabled.

## One-time setup

1. **Create a nuget.org API key** (https://www.nuget.org/account/apikeys) with
   **Push** scope. Glob the package ids to `Mneme*` so a single key covers all
   four (and future ids). For the very first push of a brand-new id, the key must
   allow pushing new packages (not just existing ones).
2. **Add it as a repo secret** named `NUGET_API_KEY`
   (Settings → Secrets and variables → Actions → New repository secret).
3. Confirm the ids are available / owned on nuget.org. If reserving the `Mneme.*`
   prefix, request an ID prefix reservation once the first package is live.

## Cutting a release

1. **Pick the version.** Pre-1.0 uses SemVer with a prerelease label, e.g.
   `0.1.0-alpha.1`. The version lives centrally in
   [`Directory.Build.props`](../Directory.Build.props) (`<Version>`) for local
   packs, but a release **overrides it from the tag**, so the tag is the source
   of truth.
2. **Update the changelog.** Move items from `## [Unreleased]` into a new
   `## [x.y.z] - YYYY-MM-DD` section in [`CHANGELOG.md`](../CHANGELOG.md).
3. **Commit** the changelog (and, optionally, bump `<Version>` in
   `Directory.Build.props` to match, so local packs agree with the tag).
4. **Tag and push:**
   ```bash
   git tag v0.1.0-alpha.1
   git push origin v0.1.0-alpha.1
   ```
5. The **Release workflow** runs: build (warnings-as-errors) → test → pack →
   push to nuget.org (`--skip-duplicate`) → GitHub Release with auto notes and
   the `.nupkg`/`.snupkg` attached.

## Dry run (no push)

Use the **workflow_dispatch** trigger (Actions → Release → Run workflow) with a
`version` and `dry_run = true`. It packs and uploads the artifacts to the run
(for inspection) **without** pushing to nuget.org.

## Local pack (for inspection)

```bash
# default version from Directory.Build.props
dotnet pack Mneme.slnx -c Release -o artifacts

# or a specific version
dotnet pack Mneme.slnx -c Release -o artifacts -p:Version=0.1.0-alpha.1
```

Inspect a package's contents with any zip tool, or install the tool locally:

```bash
dotnet tool install -g Mneme.Mcp --add-source ./artifacts --version 0.1.0-alpha.1
mneme-mcp --help
dotnet tool uninstall -g Mneme.Mcp
```

## Consuming the published packages

```bash
dotnet add package Mneme.Contracts --prerelease
dotnet add package Mneme --prerelease
dotnet add package Mneme.Agents.AI --prerelease
dotnet tool install -g Mneme.Mcp --prerelease
```

## Notes

- **Unsigned packages are fine.** `dotnet nuget verify --all` reports "not
  signed" locally; nuget.org applies a repository signature on publish. Author
  signing is optional and not currently configured.
- **Prerelease dependency.** `Mneme.Agents.AI` depends on a preview of
  `Microsoft.Agents.AI.Abstractions`; that is expected while MAF is in preview
  and keeps our own package prerelease-labeled.
- **Yank/deprecate** via the nuget.org UI if a bad version ships; do not delete
  (immutability). Cut a new patch instead.
