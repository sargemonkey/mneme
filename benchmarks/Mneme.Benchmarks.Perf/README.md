# Mneme.Benchmarks.Perf

**Performance** microbenchmarks (ingest throughput + query latency) built on
[BenchmarkDotNet](https://benchmarkdotnet.org/).

> Not to be confused with [`Mneme.Benchmarks`](../Mneme.Benchmarks/), which
> measures retrieval **quality** (LoCoMo / LongMemEval recall). This project
> measures **speed**.

## Run

```pwsh
# All benchmarks (full statistical run — takes a few minutes):
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.Perf

# One class:
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.Perf -- --filter *IngestBenchmarks*

# Quick smoke run (fewer iterations, rough numbers):
dotnet run -c Release --project benchmarks/Mneme.Benchmarks.Perf -- --job short
```

> **Always run in `-c Release`.** BenchmarkDotNet refuses to produce
> trustworthy numbers from a Debug build.

## What it measures

### `IngestBenchmarks`

Full sync ingest pipeline — validate → redact → classify → WAL commit →
post-commit observers (projections + FTS index + feedback). This is the
`<50ms` p99 path the locked sync/async split promises.

- `IngestBatch` — parameterized by `BatchSize` (1 and 100). The single-event
  number is per-event latency; the batch number divided by 100 is steady-
  state throughput.

### `QueryBenchmarks`

Read-path latency against a store pre-populated with `StoreSize` events
(1,000 and 10,000), so you can see how latency scales with corpus size.

- `FreeTextQuery` *(baseline)* — FTS5 free-text with adaptive BM25 + recency.
- `FreeTextQueryExplain` — same, with score-decomposition diagnostics on.
- `CategoryQuery` — structured category filter, no FTS term.
- `ListRecent` — the 25-most-recent hot path (used by dedupe checks).

## Notes

- Each run uses a throwaway **on-disk** SQLite database under the temp
  directory (deleted on cleanup). On-disk is deliberate: WAL commit latency
  to a real file is what production hosts actually pay.
- `[MemoryDiagnoser]` is on, so allocation columns appear alongside timings.
- These benchmarks are **not** part of `dotnet test` and don't run in CI —
  they're a manual, on-demand developer tool. Results are machine-specific;
  commit numbers only with the hardware noted.

## Findings to date

The first run of this harness immediately surfaced a real regression:
`CategoryQuery` at 10,000 events ran in **~17 ms** because the
single-category structured query used `category IN (SELECT … json_each)`,
which forced SQLite into a `USE TEMP B-TREE FOR ORDER BY` (a full sort of
every matching row) instead of serving `ORDER BY valid_at` from
`idx_memory_events_category`. Switching the single-category case to an
equality predicate (`category = $cat`) dropped it to **~112 µs at 10k**
(~150× faster) and made latency flat across corpus size. Fixed in
`src/Mneme/Query/MemoryQueryApi.cs`; guarded by
`MemoryQueryApiTests.Query_single_category_filter_returns_only_that_category`.

| Benchmark | Store | Before | After |
|---|---|---|---|
| `CategoryQuery` | 10,000 | ~17,000 µs | ~112 µs |
| `FreeTextQuery` (baseline) | 10,000 | ~1,040 µs | ~1,040 µs |
| `ListRecent` | 10,000 | ~113 µs | ~113 µs |
| `IngestBatch` (per event) | — | ~1.4 ms | ~1.4 ms |

*(Numbers from a `--job short` run on the dev machine; treat as indicative,
not authoritative. Re-run a full job for real reporting.)*

