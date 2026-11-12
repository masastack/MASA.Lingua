# Changelog

All notable changes to MASA.Lingua are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/).

[简体中文](CHANGELOG.zh-CN.md)

## [Unreleased]

## [0.0.1-preview.2]

Packaging fixes shipped after the `0.0.1-preview.1` packages were pushed (the `0.0.1-preview.1` packages on nuget.org were built from the initial release tag and do not include these changes).

### Added

- Key-path conflict detection in the publish gate (`KEY_PATH_CONFLICT`): a root bare key coexisting with a same-named namespace (or duplicate full paths) would silently lose one side's text in nested publish JSON. A new `BlockOnKeyPathConflicts` policy switch (default: on) lets projects that consume the flat format opt out; gate reports list at most 5 conflicts, then summarize the rest.
- Publish manifest: each culture now carries an optional `flatUrl` for the flat-format bundle (`?format=flat`).

### Fixed

- Bundle ETag / version fingerprint now hashes nested and flat JSON together: for key-path-conflict entries whose text only appears in the flat file, editing the translation previously left the fingerprint (and every client ETag) unchanged, so clients never received the update.
- .NET SDK packages (`AspNetCore` / `MasaBlazor` / `Maui`) now prefer the flat-format bundle when downloading (manifest `flatUrl`, or `?format=flat` for SSE-driven syncs), falling back to the nested URL when the server does not provide it.

## [0.0.1-preview.1]

First public preview of MASA.Lingua, open-sourced under the Apache License 2.0.

### Added

- Blazor admin UI: translation entry matrix, namespace and culture management, glossary and import/export tooling.
- Deterministic bundle compiler producing nested/flat `{culture}.json` with SHA-256-based ETags; published bundles restore byte-for-byte across server restarts.
- Distribution endpoints with strong ETag / `Cache-Control: no-cache` / 304 semantics, plus an SSE publish stream with `Last-Event-ID` catch-up.
- Global shared namespaces (`common` / `errors`) merged into per-project bundles with layered fallback.
- Import endpoint (`import-json`) with optional immediate publish and a 10 MB request cap.
- AI-assisted batch translation via Microsoft.Extensions.AI (any OpenAI-compatible endpoint), gated by deterministic validation.
- Glossary enforcement validated at entry and publish time, with compliance reporting.
- Publish policy gates with explicit `publishBlockedReason` reporting.
- PostgreSQL-only persistence, with a standalone EF migrations package (`MASA.Lingua.Storage.Migrations.PostgreSql`).
- Missing-key telemetry gated by per-project write keys (`X-Lingua-Key`); only server-side SDKs report.
- `lingua-sync` CLI (`pull` / `push` / `check`) for build-time bundle pulls, bulk import and CI checks, plus the `@lingua/vue` Vue 3 adapter on npm.
- Six NuGet packages sharing one version number:
  - `MASA.Lingua.Core` — i18n core domain, bundle compiler, JSON converter, and deterministic validation engine.
  - `MASA.Lingua.SDK.Abstractions` — lightweight client abstractions shared by the SDK packages.
  - `MASA.Lingua.SDK.AspNetCore` — ASP.NET Core `IStringLocalizer` adapter over local `{culture}.json` files, with FrozenDictionary lookups, file watching, and ETag/SSE hot reload (polling fallback).
  - `MASA.Lingua.SDK.MasaI18n` — bridge to MASA Framework I18n (`II18n` / `I18n.T`); synced bundles are written to `{LocalJsonDirectory}/.lingua-cache/`, while the shipped `{culture}.json` files stay untouched as a frozen fallback.
  - `MASA.Lingua.SDK.MasaBlazor` — language resource adapter for Blazor (Server/Wasm) + Masa.Blazor: merges from a shared hot-reloading store when present, otherwise pulls published bundles at startup; static `wwwroot/i18n` JSON remains the offline fallback.
  - `MASA.Lingua.SDK.Maui` — localizer for .NET MAUI / Blazor Hybrid / WPF clients, syncing at startup and on resume; no MAUI workload dependency, usable from any .NET 8/10 client app.
