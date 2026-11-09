# Changelog

All notable changes to MASA.Lingua are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/).

[简体中文](CHANGELOG.zh-CN.md)

## [Unreleased]

- Open-sourced the repository under the Apache License 2.0.
- Moved NuGet package publishing from the internal CI to GitHub Actions (`package_push_nuget.org.yml`, triggered by GitHub Releases).
- Site deployment stays on the internal GitLab, fed by a GitHub-to-GitLab mirror workflow.

## [1.3.0]

### Added

- `MASA.Lingua.SDK.Maui` — localizer for .NET MAUI / Blazor Hybrid / WPF clients, syncing at startup and on resume.
- Project **write keys**: the missing-key telemetry endpoint now requires a per-project write key (`X-Lingua-Key`); only server-side SDKs report.
- Standalone PostgreSQL migrations package (`MASA.Lingua.Storage.Migrations.PostgreSql`).

### Changed

- Storage is PostgreSQL-only; the SQLite path was removed.
- Publish artifacts and bundle ETags are fully deterministic — server restarts restore published bundles byte-for-byte from database snapshots.

## [1.2.0]

### Added

- `lingua-sync` CLI (`pull` / `push` / `check`) for build-time bundle pulls, bulk import and CI checks.
- Import endpoint (`import-json`) with optional immediate publish and a 10 MB request cap.
- Global shared namespaces (`common` / `errors`) merged into per-project bundles with layered fallback.

## [1.1.0]

### Added

- AI-assisted batch translation via Microsoft.Extensions.AI (any OpenAI-compatible endpoint), gated by deterministic validation.
- Glossary enforcement: mandatory terms validated at entry and publish time, with compliance reporting.
- Publish policy gates with explicit `publishBlockedReason` reporting.

## [1.0.0]

### Added

- Initial release: Blazor admin UI, translation entry matrix, namespace/culture management.
- Deterministic bundle compiler producing nested/flat `{culture}.json` with SHA-256-based ETags.
- Distribution endpoints with strong ETag / `Cache-Control: no-cache` / 304 semantics.
- SSE publish stream with `Last-Event-ID` catch-up.
- `MASA.Lingua.SDK.AspNetCore` (`IStringLocalizer` adapter with SSE + polling hot reload) and `MASA.Lingua.SDK.MasaI18n` (MASA Framework bridge).
