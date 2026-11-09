# @lingua/vue

Lightweight Vue 3 i18n adapter for [MASA.Lingua](../../README.md): fixed `{culture}.json` static bundles, ETag/304 negotiation, and hot updates over SSE — plus the `lingua-sync` CLI for build-time pulls, bulk imports and CI checks.

[中文文档（完整接入指南）](../../docs/integration/vue.md)

## Install

```bash
npm i @lingua/vue vue-i18n
```

Requires Vue 3 + vue-i18n v9/v10 (Composition API mode, `legacy: false`) and Node >= 18 for the CLI.

## Usage

```ts
// src/i18n.ts
import { createI18n } from 'vue-i18n';
import { setupLinguaVue } from '@lingua/vue';

export const i18n = createI18n({
  legacy: false,
  locale: 'zh-CN',
  fallbackLocale: 'zh-CN',
  messages: {},
});

export const lingua = setupLinguaVue(i18n, {
  endpoint: import.meta.env.VITE_LINGUA_ENDPOINT, // e.g. https://lingua.example.com
  project: 'shop-vue',
});
```

```ts
// src/main.ts
const locale = i18n.global.locale.value;
await lingua.syncLocale(locale);              // load before first render to avoid flashing keys
if (locale !== 'zh-CN') await lingua.syncLocale('zh-CN');  // fallback locale

createApp(App).use(i18n).mount('#app');

lingua.startHotUpdate();                      // optional: SSE + polling, no page refresh after publish
```

No Vite proxy needed — Lingua serves `Access-Control-Allow-Origin: *` for machine reads.

### Options

| Option | Default | Description |
|---|---|---|
| `endpoint` | — (required) | Lingua server address |
| `project` | — (required) | Project code on the platform |
| `staticBundles` | — | Bundled-in local `locales/{culture}.json` for a 0 ms first paint |
| `pollIntervalMs` | `60000` | Manifest polling interval for hot update; `0` disables polling |
| `enableServerEvents` | `true` | Subscribe to the server SSE publish stream |
| `reportAdoption` | `true` | Send `X-Lingua-Client` / `X-Lingua-Version` headers for adoption stats |
| `onUpdated` | — | Callback after a hot update (`{ version, cultures }`) |

`setupLinguaVue` never loads locales on its own — call `syncLocale(culture)` yourself. It returns `true` when fresh content arrived (HTTP 200) and `false` on 304 / not-yet-published; network errors **throw**, so wrap the first paint call in try/catch.

The adapter deliberately does **not** report missing keys — a write key cannot be safely kept in a browser, and the server only accepts reports carrying it. Missing translations on the frontend are surfaced by the server-side SDK telemetry and `lingua-sync check`.

## lingua-sync CLI

Zero-dependency build-time / CI tool (Node 18+):

```bash
npx lingua-sync pull    # fetch published bundles, write {culture}.json + lingua.lock.json
npx lingua-sync push    # import a legacy locales directory as a draft (default namespace: common)
npx lingua-sync check   # coverage / placeholder checks + source-code key scan
```

Common options (also settable in `lingua.config.json`):

| Option | Env | Description |
|---|---|---|
| `--endpoint <url>` | `LINGUA_ENDPOINT` | Server address (default `http://localhost:5000`) |
| `--project <code>` | `LINGUA_PROJECT` | Project code |
| `--cultures a,b` | — | Restrict to these cultures |
| `--out <dir>` | — | Local bundle directory (default `src/locales`) |

`push` adds `--dir` (source locales directory) and `--publish` (publish immediately, subject to the publish gate); `check` adds `--remote`, `--src`, `--min-coverage` and glossary checks. Full reference: `lingua-sync --help` and [docs/integration/cli.md](../../docs/integration/cli.md).

## Development

```bash
npm install
npm test   # node --test test/*.test.mjs
```

## License

Apache-2.0 — see the repository [LICENSE](../../LICENSE).
