# Contributing to MASA.Lingua

Thanks for your interest in contributing! This document covers the development environment, how to build and test, and what to include in a pull request.

[简体中文](CONTRIBUTING.zh-CN.md)

## Development environment

| Requirement | Version | Notes |
|---|---|---|
| .NET SDK | 10.0 | pinned via [global.json](global.json); SDK packages target `net8.0;net10.0` |
| Node.js | >= 18 | only for `packages/lingua-vue` (adapter + `lingua-sync` CLI) |
| Docker | any recent | required by integration tests ([Testcontainers](https://dotnet.testcontainers.org/) runs a real PostgreSQL) |

## Build and test

```bash
# Build the whole solution
dotnet build -c Release

# Run the .NET test suite (integration tests spin up PostgreSQL via Testcontainers)
dotnet test -c Release

# Run the lingua-vue test suite
cd packages/lingua-vue && npm install && npm test
```

Tests use [Testcontainers.PostgreSql](https://www.nuget.org/packages/Testcontainers.PostgreSql) — no local database setup is needed, but a working Docker daemon is required.

## Project layout

```
src/
  MASA.Lingua.Core/               # domain + bundle compiler + validation (dual-target net8/net10)
  MASA.Lingua.AI/                 # AI batch translation (Microsoft.Extensions.AI)
  MASA.Lingua.Server/             # Blazor admin UI + distribution endpoints (net10)
  Storage/
    MASA.Lingua.Storage.EfCore/   # EF Core repositories and seed data
    MASA.Lingua.Storage.Migrations.PostgreSql/   # EF migrations (PostgreSQL only)
  SDK/                            # client packages (dual-target net8/net10)
packages/lingua-vue/              # Vue 3 adapter + lingua-sync CLI (Node)
tests/MASA.Lingua.Tests/          # xunit; server tests run against a real PostgreSQL container
```

Constraint worth knowing before you touch [MASA.Lingua.Core](src/MASA.Lingua.Core) or any `SDK.*` project: they must compile with **net8-available APIs only** — net10-only capabilities belong in `Server` / `Storage.*` / `AI`.

## Code style

- `Nullable` and `ImplicitUsings` are enabled; `LangVersion` is `latest` (see [Directory.Build.props](Directory.Build.props)).
- File-scoped namespaces, `sealed` types where possible, and XML doc comments on public APIs — the codebase deliberately documents *why*, not just *what*. Please keep that style.
- Comments in this repository are written in Chinese; matching that is appreciated but not required for contributions.
- `dotnet format --verify-no-changes` runs in CI (currently advisory); running `dotnet format` locally before pushing keeps the diff clean.

## Pull requests

1. Fork / create a branch from `main`.
2. Add or adjust tests for behavior changes — the existing suite covers distribution, validation, storage and SDK behaviors; bug fixes should include a regression test.
3. Update docs under [docs/](docs) when you change public behavior (endpoints, config keys, SDK contracts).
4. Keep PRs focused; one logical change per PR.
5. CI must pass: `pr_run_test_ci.yml` builds the solution, runs both test suites, and checks formatting.

## Releasing (maintainers)

1. Update `Version` in [Directory.Build.props](Directory.Build.props) (single source for all 6 packages).
2. Update [CHANGELOG.md](CHANGELOG.md).
3. Tag the commit with the bare semver (e.g. `1.3.1`, no `v` prefix) and publish a GitHub Release.
4. `package_push_nuget.org.yml` packs all packages and pushes them to nuget.org via NuGet OIDC login.
5. Site deployment stays on GitLab: pushes to `main` are mirrored by `mirror-gitlab.yml` and deployed by `.gitlab-ci.yml`.
