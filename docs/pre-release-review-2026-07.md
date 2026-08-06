# Pre-release review — 2026-07-11

Final performance + security review before the first public NuGet release.
Both a security specialist and a performance pass reviewed the shippable
library code (`src/`). Summary of findings and what was done.

## Security

**Result: clean, one LOW finding (fixed).**

| Finding | Severity | Status |
|---|---|---|
| Sidecar bearer-token compared with non-constant-time `string.Equals` (timing side-channel on the auth path) — `src/Mneme.Sidecar/Program.cs` | LOW | ✅ Fixed — now `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes. |

Reviewed and confirmed clean:
- **SQL injection** — all raw SQL is `$`-parameterized; the only interpolated
  SQL fragments are built from booleans/loop indices, never caller input. FTS5
  `MATCH` input is sanitized (alphanumeric tokens, quoted, OR-joined). The
  `projection_fact_triples` LIKE queries bind their values and are
  workstream-scoped.
- **Capability enforcement** — `CapabilityEnforcement.Enforce` on every public
  query/distill entry point; cross-workstream requires an explicit grant;
  curation enforces per-op grants + target-workstream scope; no raw-SQL escape
  hatch on the public surface.
- **Secret handling** — redaction runs before persist and covers all payload
  variants including `FactPayload.Triples` subject/object. *(Post-review
  follow-up: DI-wired hosts were registering the redactor via a constructor
  that resolved to an **empty** rule set — a silent no-op — so redaction
  stripped nothing on those ingest paths. Fixed to construct the default rule
  set on every path; see CHANGELOG "Fixed: ingest secret redaction was a
  silent no-op in DI-wired hosts" and its regression test.)*
- **Deserialization** — `System.Text.Json` polymorphism is a closed
  `[JsonDerivedType]` set; the `$type` discriminator cannot instantiate
  arbitrary types.
- **MCP server** — all tools flow through the capability-checked APIs; no
  raw-SQL/unscoped tool.
- **Path handling** — sync-store keys are engine-generated; SQLite path is
  trusted config.

## Performance

**Result: applied the clearly-beneficial, low-risk optimizations; measured to
separate real wins from a misdiagnosed "critical."**

Fixed:
- **Subject-key lookups batched** — `LoadSubjectScopedEvents` /
  `LoadSubjectTripleSupplement` now issue one OR-joined query instead of one
  per query subject key.
- **Fact-triple projection** — reuses one prepared INSERT command across a
  fact's triples instead of allocating/preparing a command per triple (ingest
  hot path).
- **Candidate hydration batched** — `FreeTextSearch` / `HybridSearch` load all
  candidate rows in a single `json_each` query, then gate in-memory, instead of
  one SELECT per candidate. All bi-temporal/capability/channel/revocation
  gating is unchanged (verified by the full query test suite).
- **`SubjectKey.ExtractSubjects`** — `HashSet` dedup instead of `List.Contains`.

Measured (5,000 events, 25-limit hybrid query): **~20 ms/query**, ~28–30 ms with
the subject-triple supplement. Notably, the candidate-hydration batching was
**net-neutral on wall-clock** — confirming the per-candidate SELECT was *not*
the bottleneck (embedded SQLite PK lookups on an open connection are
microseconds; there are no network round-trips). The batching is retained
anyway: it removes ~150 command allocations per query and scales better as the
pool grows. The dominant per-query cost at this scale is the **brute-force
vector scan** (`VectorIndex` loads all stored vectors per query) — the
documented, intended Phase-11 tradeoff, acceptable at v1 (LoCoMo) scale.

Deferred to v0.2 (documented, non-blocking):
- `VectorIndex` vector caching / `sqlite-vec` native index for large corpora
  (the real lever once workstreams exceed ~tens of thousands of events).
- `PRAGMA cache_size` / `mmap_size` tuning (~5–10% latency).
- Micro-allocation cleanup in the fusion merge.

## Verdict

No blocking issues for a v0.1-alpha public release. 344/344 tests pass after
the changes.
