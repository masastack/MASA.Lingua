# Deployment

> [简体中文](deployment.zh-CN.md) · [Back to docs index](README.md)

Production checklist for deploying the Lingua server. Client SDK deployment is covered in the [integration guides](integration/README.md).

## Requirements

| Item | Requirement | Notes |
|---|---|---|
| Runtime | .NET 10 | server closure (`Server` / `Storage.*` / `AI`) targets net10 |
| Database | PostgreSQL (only) | connection string from `Lingua:Database:ConnectionString` (or `ConnectionStrings:LinguaDb`) |
| Replicas | **single instance** | publish cache, manifest stats and SSE broadcast are in-process state; multi-replica deploys silently break (see [architecture](architecture.md#single-instance-constraint)) |
| Bundle directory | `Lingua:BundleDirectory` (default `{ContentRoot}/App_Data/i18n`) | restart-recovery mirror only, never exposed publicly (`/i18n/*` always goes through the distribution endpoints) |
| Admin auth | SSO login (mandatory) | `Auth:ClientId` + DCC (`DCC` section; MasaStackConfig resolves the SSO domain); startup fails otherwise. `Auth:Authority` overrides for tests/offline debugging only |
| HTTPS | required for the admin UI | terminate TLS at your ingress + `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` |

## Container image

The [Dockerfile](../src/MASA.Lingua.Server/Dockerfile) is production-oriented:

- Runs as non-root, pinned to **UID 1000** (`appuser`) to match the common volume-ownership convention.
- Built-in writable directories under `/data` (mode `1777`, writable by any runtime UID):

| Path | Purpose | Persistence |
|---|---|---|
| `/data/i18n` | published bundles (env `Lingua__BundleDirectory`) | disposable — restored from DB snapshots on rebuild |
| `/data/dataprotection` | DataProtection key ring (auth cookies, OIDC state) | **must be a volume** — without it every container rebuild logs everyone out |
| `/data/logs` | file logs + crash records (`crash.log`) | volume recommended, survives restarts |
| `/data/dumps` | minidumps on unhandled exceptions (`DOTNET_DbgMiniDump*`) | volume recommended |

- `readOnlyRootFilesystem: true` is supported: mount the paths above as volumes/`emptyDir` (K8s: `fsGroup` helps with volume ownership).
- Ports: **8080** (HTTP) and 8081 (HTTPS), from `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_HTTPS_PORTS`.

## HTTPS and forwarded headers

The admin UI's OIDC correlation/nonce cookies carry the `Secure` flag — over plain HTTP the browser rejects them and **login cannot complete**. Set:

```yaml
env:
  - name: ASPNETCORE_FORWARDEDHEADERS_ENABLED
    value: "true"   # adopt X-Forwarded-Proto from the ingress
```

The 80→443 redirect belongs at the edge (ingress-nginx defaults `ssl-redirect: true`). If the container cannot discover an https endpoint and you need a fallback, set `ASPNETCORE_HTTPS_PORT=443`.

## Memory and resilience

The image ships with GC settings tuned for the import/publish memory spikes:

```dockerfile
ENV DOTNET_GCHeapHardLimitPercent=50   # hard heap cap at half the cgroup limit — fail GC, not OOMKill
ENV DOTNET_GCDynamicAdaptationMode=0   # DAC heuristics lag behind spike-shaped allocation
```

Unhandled exceptions produce a minidump in `/data/dumps` and a `crash.log` in `/data/logs`; file logs roll daily (14 retained, 50 MB cap) so post-mortem analysis survives pod restarts.

## Access model

- Machine-facing **read** endpoints (`/i18n/*`, manifest, glossary, SSE) are anonymous and CORS-open (`Access-Control-Allow-Origin: *`, `ETag` exposed). Deploy on a trusted network; published bundles are public by design.
- **Write** endpoints are separate controls: project create/delete and import are network-gated (keep the port off the public internet), missing-key telemetry requires the project write key and is the only authenticated machine endpoint.
- The admin UI (`/`, `/glossary`, `/import-export`) requires SSO login — independent of the machine endpoints.

## CI/CD topology

- **GitHub** hosts the source of truth: PR CI (`pr_run_test_ci.yml`), NuGet publishing on GitHub Release (`package_push_nuget.org.yml`).
- **GitLab** receives a mirror (`mirror-gitlab.yml`) and runs the deployment pipeline (`.gitlab-ci.yml`): image build → `push-sec` → `push-prd` via the MASA Stack ArgoCD templates.
