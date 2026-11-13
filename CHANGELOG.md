# Changelog

All notable changes to MASA.Lingua are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/).

[简体中文](CHANGELOG.zh-CN.md)

## [Unreleased]

### Added

- Optional shared write key `Lingua:Distribution:WriteApiKey` (environment variable `Lingua__Distribution__WriteApiKey`). When set, the write-style endpoints — `POST` / `DELETE /api/v1/projects/{project}`, `import-json` and `export-zip` — require a matching `X-Lingua-Key` header (`401` when missing, `403` when it does not match; constant-time comparison). Empty or whitespace (the default) keeps them anonymous as before. Read endpoints and missing-key telemetry are unaffected, and it is a different secret from the per-project write key. Use a long random value and inject it from the environment — do not put it in a committed `appsettings`.
- `lingua-sync push --key <secret>` / `LINGUA_WRITE_KEY` sends the shared write key. The CLI deliberately does not read it from `lingua.config.json`; a `401` / `403` now tells the user how to supply or fix it.
- `GET /admin/projects/{project}/export-zip`: the admin UI's "download all cultures" button now points here. It is protected by the admin SSO policy (anonymous requests are redirected to `/login`), because a browser navigation cannot send `X-Lingua-Key` and would otherwise stop working as soon as `WriteApiKey` is set. The `/api/v1/.../export-zip` route keeps working for scripts.
- `StaticBundlePublisher.DeleteProjectAsync` deletes a project and purges its published state in one call; the delete endpoint uses it so the two steps cannot drift apart.
- `LinguaCultureFiles.IsSafeCultureName` (`MASA.Lingua.SDK.Abstractions`): the shared file-name safety rule for culture names, used by the MAUI and ASP.NET Core SDKs.
- `LinguaLocalizer` (`MASA.Lingua.SDK.Maui`) accepts an optional `ILogger<LinguaLocalizer>` (`AddLinguaMaui` supplies it from the container) and logs a warning for every failed sync.
- Key / namespace path conflicts are now caught **when they are created**, not only at publish time. A root key that collides with a same-named namespace (`Confirm` vs `Confirm.*`) is skipped by missing-key telemetry (it cannot be written without breaking the nested bundle), flagged in the admin UI's "add entry" dialog, and reported by `import-json` as `pathConflictCount` / `pathConflicts` (only the conflicts that this import introduced). New `IEntryCatalog.FindKeyPathConflictsAsync` (default implementation returns none), `KeyPathIndex`, `JsonImportResult.PathConflicts` / `PathConflictCount`.
- The publish check now returns non-blocking `Warnings`, shown in the admin UI: key-path conflicts that the publish policy lets through.
- `LinguaKeys` (`MASA.Lingua.Core`): the single comparison rule for namespace codes and entry keys (`OrdinalIgnoreCase`; comparers, `Fold` for database queries). `import-json` reports entries that differ only in letter case inside the uploaded file as `caseDuplicateCount` / `caseDuplicates` (`JsonImportResult.CaseDuplicates` / `CaseDuplicateCount`); `lingua-sync push` prints them.
- Snapshot content format v2 stores the nested **and** the flat bundle of every culture (`SnapshotContentCodec.Pack` / `UnpackBundles`, `BundleCompiler.FromPublishedBytes(project, culture, nested, flat)`); the published directory gets a `{culture}.flat.json` next to `{culture}.json`. Snapshots and directories written by earlier versions are still read — see Fixed.

### Changed

- **Namespace codes and entry keys are now case-insensitive everywhere.** `Confirm` and `confirm` are the same namespace / key. Before, they were two different ones, and clients disagreed about it: the MasaBlazor I18n cache merged them while the .NET SDKs kept them apart. One rule (`LinguaKeys`, `OrdinalIgnoreCase`) now covers storage uniqueness, upsert / delete / import / missing-key lookups, shared-namespace inheritance, publish merging (the project layer overrides the shared layer by case-insensitive identity), conflict detection and every SDK lookup (`LinguaFrozenMemoryStore`, `IStringLocalizer`, the MAUI `LinguaLocalizer`, the MasaI18n bridge). Writing an existing entry with different casing updates it; the stored casing is the one used when the entry was first created, and published bundles use it.
- **Upgrade note:** migration `CaseInsensitiveKeyUniqueness` replaces the three unique indexes with `lower(...)` expression indexes. If the database already holds namespaces or keys that differ only in letter case, the migration stops with an error that lists them (up to 20 groups per kind) and changes no data — merge or delete the duplicates, then start the site again. Keys that were valid because they differed only in case are no longer allowed. Limits: project codes are unchanged; JS runtimes such as vue-i18n look keys up case-sensitively, so a Vue app has to use the published casing; `lower()` on non-ASCII letters follows the database locale.
- The site runs EF Core migrations on every start (it already did; the shape now follows MASA.Auth's `MigrateDbContextAsync`): new `MigrateLinguaDatabaseAsync` first asks for pending migrations — none means a single "up to date" log line, otherwise the pending migration names are logged and applied; on failure it logs Critical and rethrows, so the site never keeps starting on an old schema. Migration finishes before hosted services and endpoints are up, and the database-level advisory lock taken by EF Core / Npgsql makes starting several replicas at once safe. `InitializeLinguaDatabaseAsync` (migrate + seed an empty database) behaves as before.
- One canonical path rule (`JsonBundleConverter.ToPathSegments`) is now shared by the nested JSON, the flat JSON and the conflict detector, so what the gate reports is exactly what the bundle does. Namespace / key segments are trimmed, empty segments are dropped, and an entry whose path ends up empty is skipped instead of producing a `""` key. `import-json` applies the same normalization to the keys it reads.
- Draft-vs-published diff and the manifest `entryCount` now work on the flat bundle, which is the one the .NET SDKs download: a change that exists only on the flat side of a key-path conflict is visible in the diff, and `entryCount` counts published entries instead of lookup aliases.
- `MASA.Lingua.SDK.AspNetCore`: the sync service downloads the flat bundle by default (`LinguaProtocol.FlatBundlePath`) when the manifest carries no `flatUrl`; missing-key reports are trimmed.

- `MASA.Lingua.SDK.AspNetCore`: the sync service reconciles a missing local `{culture}.json` — when a culture file has been deleted it no longer sends `If-None-Match`, so a `304` can't keep the disk empty forever (same reconciliation the MAUI client already had); cultures the platform has removed no longer count as "missing". Culture names from the manifest or an SSE event are skipped (with a warning) unless they are safe file names, `SyncCultureFileAsync` throws `ArgumentException` for an unsafe name, and the write-retry wait no longer blocks a thread-pool thread.
- `MASA.Lingua.SDK.Maui`: `LastSyncError` is now recorded inside the sync gate, so concurrent syncs always leave it describing the last completed sync (a late failure can no longer overwrite a later success). Failures are also written to the logger.
- `.github/dependabot.yml` ignores semver-major updates only for `Microsoft.Extensions.Http`, `Microsoft.Extensions.Localization.Abstractions` and `Microsoft.Extensions.Hosting.Abstractions` instead of the whole `Microsoft.Extensions.*` family, so `Microsoft.Extensions.AI*` keeps receiving upgrade PRs.
- `MASA.Lingua.SDK.Maui`: `TrySyncAsync` / `InitializeAsync` now never throw for a failed network sync — only the caller's own cancellation still propagates. Previously only `HttpRequestException` and timeouts were swallowed, so a captive-portal HTML response, a full disk or a mis-typed endpoint escaped into app startup. The failure is kept in the new `LinguaLocalizer.LastSyncError` (cleared by the next successful sync); `SyncAsync` still throws and also records it.
- `MASA.Lingua.SDK.Maui`: a downloaded culture bundle is now validated **before** anything is committed, then written atomically, and only then folded into memory — so neither failure direction can leave the app and the disk disagreeing. A 200 response whose body is not valid JSON can no longer overwrite a good cached file, and a cache write that fails (full disk, permissions, AV lock) no longer leaves the new strings live in memory and the old ones on disk. In both cases the old cache, the in-memory terms and the ETag stay untouched and the next sync retries. `Seed` follows the same order.
- `MASA.Lingua.SDK.Maui`: `AddLinguaMaui` validates its options at registration — a missing `ProjectCode`, or a non-empty `ServerEndpoint` that is not an absolute `http(s)` URL, throws `InvalidOperationException` (an empty endpoint remains the valid offline / seed-only mode). The `LinguaClient` named `HttpClient` now has a 30 second request timeout (`LinguaMauiExtensions.DefaultRequestTimeout`), which an app can override with its own `ConfigureHttpClient`.
- `MASA.Lingua.SDK.Maui`: `Seed` normalizes the culture name (`zh-cn` → `zh-CN`) before choosing the cache file name, so Android (case-sensitive file system) and iOS (case-insensitive by default) can no longer end up with diverging caches.
- `net8.0` targets of `MASA.Lingua.SDK.Maui` and `MASA.Lingua.SDK.MasaBlazor` now depend on `Microsoft.Extensions.*` 8.0.x (`Http` 8.0.1, `Localization.Abstractions` 8.0.34, `Hosting.Abstractions` 8.0.1) instead of 10.0.14, so .NET 8 apps are no longer forced to upgrade the whole `Microsoft.Extensions.*` family. `net10.0` targets are unchanged. Dependabot is configured to ignore major-version updates of those three packages.

### Fixed

- **A root key and a namespace with the same name (for example key `Confirm` plus namespace `Confirm`) lost its translation in the client after a server restart, a rollback or a disk restore.** Those paths rebuilt the flat bundle from the nested JSON, in which a path can only be a value *or* an object, so the root key vanished from the flat bundle and its ETag changed. Both representations are now persisted (snapshot v2, `{culture}.flat.json`) and restored byte-for-byte, with the same ETag as before. Snapshots written by earlier versions are reconciled once at startup by recompiling the draft, but only if the result has exactly the version hash that was published; otherwise the old content is restored as it was and a warning is logged.
- `MASA.Lingua.SDK.AspNetCore`: the typed `IStringLocalizer<T>` finds a namespace such as `Confirm` for `IStringLocalizer<Confirm>` (namespace and key matching is case-insensitive). Missing keys are still reported with the lower-case type-name prefix.
- `MASA.Lingua.SDK.MasaBlazor`: if the server still sends keys that differ only in letter case (an older server), they collapse into one entry in the case-insensitive I18n cache; this is now logged as a warning instead of happening silently.
- A corrupt `{culture}.flat.json` no longer fails startup: it is ignored (with a warning) and the flat bundle is rebuilt from the nested file.
- `DELETE /api/v1/projects/{project}` now also purges the publisher's state for that project: the in-memory bundle cache, the manifest statistics, the SSE replay record and the `App_Data/i18n/{code}` directory. Previously the cache survived the delete, so `/i18n/{code}/{culture}.json` kept answering 200 for a deleted project, and a project re-created under the same code served the previous project's strings before it had been published.
- Files on disk are only trusted for a project the database says has been published (`LastPublishedAt`). A never-published project no longer serves — or restores at startup from — orphaned `{culture}.json` files, so it gets its bootstrap publish and its draft diff is no longer compared against stale content. A publish now also deletes `{culture}.json` files that do not belong to the live version.
- `POST /api/v1/projects/{project}`: when two callers create the same new project concurrently, only the one that actually inserted the row gets `created: true` and the write key; the other gets `created: false` without a key. New `IProjectCatalog.CreateProjectIfAbsentAsync` reports whether the row was created by the call.
- Bundle / manifest requests for a legacy project whose code predates the code validation now return 404 instead of 500, and changing such a project's cultures no longer fails on the publish-directory check.
- Missing-key telemetry records the reporting culture in the entry's context (the draft translation is still created only in the project's default culture, because the reported default text is source-language text, not a translation).
- `If-None-Match` handling strips exactly one `W/` prefix instead of trimming any leading `W` or `/` characters.
- Concurrent cold-path requests can no longer refill the bundle cache for a project while it is being deleted (a purge epoch invalidates restores that started before the purge). A failed atomic write (publisher and MAUI cache) no longer leaves a `.tmp` file behind.
- Admin UI: creating a local config for a project code that fails validation, or any other failure, now shows a toast (and logs the unexpected ones) instead of surfacing as an unhandled exception.

### Security

- Project codes are validated everywhere a project is created — the HTTP endpoint, the repository and the publisher (`LinguaProjectCodes`): only ASCII letters, digits, `-` and `_`, 1–64 characters, no trimming. A code becomes the published directory name (`App_Data/i18n/{code}`), so an anonymous caller could previously have created a project whose bundles were written outside the `i18n` directory (path traversal). The publisher keeps a defensive check for projects stored before this validation existed.
- Culture names that become local file names in the MAUI and ASP.NET Core SDKs are restricted to letters, digits, `-` and `_` (35 characters at most), so a malformed or tampered manifest cannot write outside the local cache directory.
- Database connection strings and DCC Redis passwords have been removed from the bundled `appsettings*.json` files (they are supplied through environment variables or user-secrets, see the integration guide). **Credentials that were present in earlier revisions remain in git history — rotate them.**

### Documentation

- Key / namespace duplication rules documented in the integration README, the MAUI guide and the architecture documents (English and Chinese): what counts as a conflict (`Confirm` key + `Confirm.*` namespace; `ns Confirm` / `key Confirm` is not one), how it is detected and blocked, why the flat bundle is the one the .NET SDKs read, the case-insensitivity rule for namespaces and keys, and the snapshot v2 / `{culture}.flat.json` layout.
- Integration guide, CLI guide, architecture and quick start: documented the shared write key (strength advice, `lingua-sync push --key` / `LINGUA_WRITE_KEY`, 401 / 403 troubleshooting), the SSO admin export route, the project-code rules, and the ASP.NET Core sync reconciliation. Corrected the MAUI test count (README and `maui.md` disagreed; both now say 27).
- MAUI guide: documented the threading contract (change notifications are raised on the context that called the sync method — the SDK deliberately does not use `ConfigureAwait(false)`, guarded by a regression test), `LastSyncError`, the registration-time validation and timeout, not awaiting `InitializeAsync` on the startup path, persisting the user's chosen culture in the app, and an unverified iOS note about `CFBundleLocalizations`. The list of things that were not verified on a device was extended.

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
