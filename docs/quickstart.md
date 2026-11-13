# Quick start

> [简体中文](quickstart.zh-CN.md) · [Back to docs index](README.md)

Go from an empty database to a translated, published string consumed by a client — about 5 minutes. The admin UI requires SSO, but every step here uses the anonymous machine API, so no SSO setup is needed.

## 1. Start PostgreSQL and the server

```bash
# PostgreSQL (the only supported database)
docker run -d --name lingua-pg \
  -e POSTGRES_PASSWORD=lingua -e POSTGRES_DB=lingua \
  -p 5432:5432 postgres:16
```

```bash
# Lingua server (dev run from the repo; requires the .NET 10 SDK)
dotnet run --project src/MASA.Lingua.Server -- \
  Lingua__Database__ConnectionString="Server=localhost;Port=5432;User Id=postgres;Password=lingua;Database=lingua;" \
  Auth__Authority=https://sso.invalid \
  Auth__ClientId=local
```

`Auth__Authority` overrides the MASA Stack DCC/SSO lookup so the process can start without a MASA Stack deployment. The admin UI login will not work (there is no real SSO behind the override) — that is fine here: the machine API used below is anonymous.

The HTTP port comes from [launchSettings.json](../src/MASA.Lingua.Server/Properties/launchSettings.json): `http://localhost:62641`.

> An empty database is seeded on first boot with three demo projects (`auth-web-staging`, `auth-sso-staging`, `shop-vue`).

## 2. Create a project

```bash
curl -X POST http://localhost:62641/api/v1/projects/demo
# {"project":"demo","defaultCulture":"zh-CN","created":true,"writeKey":"..."}
```

- If the server sets `Lingua:Distribution:WriteApiKey`, add `-H "X-Lingua-Key: <that key>"` to the create, import and delete calls (`401` without it, `403` if it does not match). It is a shared deployment key, not the per-project `writeKey` below.
- Project codes may only contain ASCII letters, digits, `-` and `_` (1–64 characters); anything else is rejected with `400`.
- Idempotent: creating an existing project returns `200` with `created: false` and changes nothing.
- `writeKey` (returned only on creation) authenticates missing-key telemetry; save it if you plan to use server-side SDK telemetry.
- Default cultures: `zh-CN` default, `zh-CN` / `en-US` / `ja-JP` supported. Cultures are extended automatically at import time.

## 3. Import and publish

```bash
curl -X POST "http://localhost:62641/api/v1/projects/demo/import-json?culture=en-US&publish=true" \
  -H "Content-Type: application/json" \
  -d '{
    "greeting": "Hello, {name}!",
    "errors.UserNotFound": "User {userId} does not exist."
  }'
```

- `publish=true` publishes immediately; without it the import lands as a draft.
- Omitting `defaultNs` puts keys in the **project root namespace** — they are published as flat top-level keys.
- Placeholders are named (`{name}`) or positional (`{0}`).
- The request body is capped at 10 MB (`413` beyond that).

Verify the published bundle:

```bash
curl -i http://localhost:62641/i18n/demo/en-US.json
# HTTP 200, strong ETag; repeat the request with If-None-Match -> 304

curl http://localhost:62641/api/v1/projects/demo/manifest
# version fingerprint + published cultures + default culture
```

## 4. Consume from a client

### ASP.NET Core (net8.0 / net10.0)

```bash
dotnet add package MASA.Lingua.SDK.AspNetCore
```

```csharp
builder.Services.AddLinguaLocalization(options =>
{
    options.ServerEndpoint = "http://localhost:62641";
    options.ProjectCode = "demo";
});
```

Then use the standard `IStringLocalizer`:

```csharp
L["greeting", new { name = "MASA" }]   // "Hello, MASA!"
L["errors:UserNotFound", new { userId = 42 }]
```

`:` and `.` are interchangeable separators. The SDK syncs `{culture}.json` into memory and a local directory, hot-reloads on SSE publish events, and falls back to polling on a fixed interval.

### Vue 3 + vue-i18n

```bash
npm i @lingua/vue vue-i18n
```

```ts
import { createLingua, startHotUpdate } from '@lingua/vue';
// full setup: docs/integration/vue.md
```

### Other clients

| Client | Guide |
|---|---|
| MASA Framework `I18n.T` | [docs/integration/aspnetcore.md](integration/aspnetcore.md#masa-framework-i18n-桥接) |
| Masa.Blazor `T()` | [docs/integration/aspnetcore.md](integration/aspnetcore.md#masa-blazor-接入) |
| .NET MAUI / WPF | [docs/integration/maui.md](integration/maui.md) |
| CI checks / bulk import | [docs/integration/cli.md](integration/cli.md) |

## 5. Try the admin UI (needs a real MASA Stack SSO)

With a real MASA Stack, drop the `Auth__Authority` override, configure the `DCC` section (or let `MasaStackConfig` resolve it), register an OIDC client in MASA Auth, and open `https://<host>/` — the entry matrix, AI translation and glossary pages all live there. See [docs/deployment.md](deployment.md).

## Next steps

- [Architecture](architecture.md) — how bundles are compiled, published and delivered
- [Deployment](deployment.md) — production checklist (HTTPS, persistence, single-instance constraint)
- [Integration guides](integration/README.md) — per-client SDK details with verified examples
