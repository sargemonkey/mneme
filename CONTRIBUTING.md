# Contributing to Mneme

Mneme is in design phase. This document will grow.

## Right now

- **Design feedback is welcome.** Open an issue against anything in
  [`plans/`](plans/). The plan documents are deliberately concrete so they
  can be picked apart.
- **PRs against an empty codebase are not yet useful.** Hold off until
  Phase 0 (contracts) lands.
- **No CI yet.** Add nothing that depends on CI infrastructure.

## When code lands

The conventions inherited from the MuxiMuxi origin:

- .NET 8, file-scoped namespaces
- `Microsoft.Data.Sqlite` for storage; no other DB drivers in v1
- System.Text.Json for serialization
- Nullable reference types enabled project-wide
- Treat warnings as errors
- Tests use xUnit (when added)

## Code of conduct

Be precise. Be kind. Disagree with the design, not with people.

## License

By contributing you agree your contributions are licensed under
[Apache License 2.0](LICENSE), the project's license.
