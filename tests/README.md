# Tests

xUnit projects, one per shippable library.

| Project | Covers |
|---|---|
| `Mneme.Contracts.Tests` | Contract surface stability, DTO round-trips, schema-version compatibility |
| `Mneme.Tests` | Memory agent implementation: storage, projections, distillation, entity resolution |

## Status

**Empty.** Tests land as each phase implements its surface area. See
[`plans/plan.md`](../plans/plan.md).

## Conventions

- One test class per production class, mirroring the namespace structure.
- Test method naming: `MethodUnderTest_Scenario_ExpectedOutcome`.
- Use the `[Trait("Category","Integration")]` attribute for tests that
  hit a real SQLite file; default tests should be pure unit tests.
- No flaky / time-dependent tests. Inject `TimeProvider` everywhere.
- Property-based testing welcome where useful (FsCheck.Xunit, when added).
