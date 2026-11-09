<!--
  Thanks for contributing! Please keep the PR focused on one logical change.
  CI runs: dotnet build/test (Testcontainers PostgreSQL), node --test for lingua-vue, dotnet format check.
-->

## What does this PR do?

<!-- One-paragraph summary. Reference issues with "Fixes #123". -->

## Type of change

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that would cause existing behavior to change)
- [ ] Documentation update
- [ ] Refactor / chore (no behavior change)

## Checklist

- [ ] Tests added or adjusted for behavior changes (`dotnet test` / `npm test` in `packages/lingua-vue`)
- [ ] Public behavior changes (endpoints, config keys, SDK contracts) are reflected in [docs/](../docs)
- [ ] `MASA.Lingua.Core` and `SDK.*` changes stay within net8.0-available APIs
- [ ] `dotnet format` has been run locally (CI checks it, currently advisory)
- [ ] CHANGELOG.md updated when relevant
