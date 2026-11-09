# Changelog

All notable changes to MASA.Lingua are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/).

[简体中文](CHANGELOG.zh-CN.md)

## [Unreleased]

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
