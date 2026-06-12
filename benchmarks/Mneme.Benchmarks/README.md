# Mneme.Benchmarks

LoCoMo / LongMemEval-style harness for Mneme.

## Run

```pwsh
cd benchmarks/Mneme.Benchmarks
dotnet run
```

Exit code 0 on full recall, 1 otherwise.

## Fixture format

See `fixtures/sample-locomo-temporal.json`. Schema in `BenchmarkFixture.cs`.

```jsonc
{
  "name": "my-fixture",
  "workstream": "bench-ws",
  "turns": [
    { "speaker": "alice", "at": "ISO timestamp",
      "content": "...", "shouldCapture": true, "category": "Fact" }
  ],
  "probes": [
    { "question": "...", "expectedSubstring": "...",
      "asOf": "ISO timestamp (optional)" }
  ]
}
```

## The bi-temporal claim

AGENTS.md locked-decision: "Bi-temporal as Mneme's primary
differentiator. Mneme must run LoCoMo + LongMemEval to verify."
This harness is the verification step. The sample fixture has 3
"current" probes and 3 "as-of" probes; full recall demonstrates
that the bi-temporal AsOf filter correctly hides later-recorded
events.

## v0 limitations

* Hit = substring match (not LLM-judge correctness like real LoCoMo).
* No retrieval-rank metric (mrr/ndcg) yet.
* No cross-system runner (Mem0 / Zep / Letta) yet — fixtures kept
  simple so a future runner can port them.
