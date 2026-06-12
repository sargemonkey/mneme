# Mneme.Sidecar

Phase 9 — runs Mneme in its own process behind a small HTTP API.

## Endpoints

Unauthenticated:
- `GET /healthz`
- `GET /readyz` (verifies SQLite is reachable)

Authenticated (`Authorization: Bearer ${MNEME_BEARER_TOKEN}`):
- `POST /v1/events`
- `POST /v1/queries`
- `GET  /v1/recent?limit=N`
- `POST /v1/distill`
- `POST /v1/revocations`

## Run locally

```pwsh
$env:MNEME_WORKSTREAM_ID = "sidecar-demo"
$env:MNEME_SQLITE_PATH   = "$env:USERPROFILE\.mneme\sidecar.db"
$env:MNEME_USER_ID       = "sujacob"
$env:MNEME_BEARER_TOKEN  = "dev-token"
dotnet run --project src/Mneme.Sidecar
```

## Docker

```pwsh
docker build -f src/Mneme.Sidecar/Dockerfile -t mneme/sidecar:dev .
docker run --rm -p 8080:8080 -v ${PWD}/data:/data `
  -e MNEME_WORKSTREAM_ID=sidecar-demo `
  -e MNEME_USER_ID=sujacob `
  -e MNEME_BEARER_TOKEN=dev-token `
  mneme/sidecar:dev
```

Image `HEALTHCHECK` hits `/readyz`.

## Limits

- One workstream per sidecar instance.
- Single shared bearer token (sufficient for service-to-service inside
  a private network). A future follow-up could add JWT validation
  (`AddJwtBearer`) for multi-tenant.
