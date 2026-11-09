# Security Policy

[简体中文](SECURITY.zh-CN.md)

## Supported versions

| Version | Supported |
|---|---|
| 1.3.x | yes |
| < 1.3 | no |

## Reporting a vulnerability

Please do **not** report security vulnerabilities through public GitHub issues.

Instead, report them privately via [GitHub Security Advisories](https://github.com/masastack/MASA.Lingua/security/advisories/new) ("Report a vulnerability"). Include as much of the following as you can:

- Type of issue (e.g. injection, authentication bypass, information disclosure)
- Affected component (server distribution endpoints, admin UI, SDKs, CLI)
- Step-by-step reproduction or proof of concept
- Impact assessment

We aim to acknowledge reports within 5 business days and will keep you informed of the fix progress.

## Security model notes

For deployment planning, be aware of the platform's documented access model (details in [docs/deployment.md](docs/deployment.md)):

- Machine-facing **read** endpoints (published bundles, manifest, glossary) are anonymous and CORS-open by design — the server is intended for trusted networks, not for exposing the admin surface publicly.
- **Write** endpoints (project create/delete, import) are separate; the missing-key telemetry endpoint requires a project write key.
- The admin UI requires SSO (OIDC) login and must be served over HTTPS.

These are documented design decisions, not vulnerabilities; reports about them should go through regular issues first.
