# 接入 Vue 3（vue-i18n）

适用：Vue 3 + `vue-i18n` v9 / v10（Composition API 模式，`legacy: false`）。

## 前置条件

- Lingua 里已有这个项目并**发布过**（拿到项目编码，例如内置演示项目 `shop-vue`；真实业务示例是 `auth-web-staging` / `auth-sso-staging`）。
- 知道 Lingua 服务地址，例如 `https://lingua.example.com`。
- 读发布产物不需要密钥（项目写密钥只用于服务端 SDK 的缺词上报，浏览器端不涉及）。

## 1. 安装

从内部 npm 源安装：

```bash
npm i @lingua/vue vue-i18n
```

```jsonc
// package.json
"dependencies": {
  "@lingua/vue": "^1.1.0",
  "vue-i18n": "^9.0.0"
}
```

内部源地址按公司配置，写进项目根的 `.npmrc`：

```ini
registry=https://<内部 npm 源地址>/
```

如果这个包还没推到内部源，让维护方先发布（`packages/lingua-vue` 的 `package.json` 当前版本 `1.1.0`）；
在此之前**不要**用 `file:` 路径依赖接进业务仓库——那会让构建依赖开发机的目录结构，CI 上必然失败。

## 2. 初始化

```ts
// src/i18n.ts
import { createI18n } from 'vue-i18n';
import { setupLinguaVue } from '@lingua/vue';

export const i18n = createI18n({
  legacy: false,
  locale: 'zh-CN',          // 当前界面语种：由你的应用决定（浏览器语言 / 用户设置）
  fallbackLocale: 'zh-CN',  // 词条缺失时的回退，建议与平台默认语种一致
  messages: {},
});

export const lingua = setupLinguaVue(i18n, {
  endpoint: import.meta.env.VITE_LINGUA_ENDPOINT, // https://lingua.example.com
  project: 'shop-vue',
});
```

```ts
// src/main.ts
import { createApp } from 'vue';
import App from './App.vue';
import { i18n, lingua } from './i18n';

const locale = i18n.global.locale.value;
await lingua.syncLocale(locale);              // 先把当前语种拉下来再渲染，避免闪出 key
// 回退语种也加载，词条缺失时才有东西可回退：
if (locale !== 'zh-CN') await lingua.syncLocale('zh-CN');

createApp(App).use(i18n).mount('#app');

lingua.startHotUpdate();                       // 可选：发布后不刷新页面也能看到新文案
```

`setupLinguaVue` **不会自动加载任何语种**，必须自己调用 `syncLocale(culture)`。
返回 `true` 表示拿到了新内容（HTTP 200），`false` 表示 304 未变化或服务端暂时没有（项目没发布）。
网络错误会**抛出**——首屏调用建议自己 try/catch，否则一次网络抖动会让整个应用启动失败。

`vite.config` 里不需要代理：Lingua 的跨域全放开（`Access-Control-Allow-Origin: *`），开发服务器直连即可。

## 3. 使用

```vue
<script setup lang="ts">
import { useI18n } from 'vue-i18n';
const { t } = useI18n();
</script>

<template>
  <button>{{ t('order.submitBtn', { count: 3 }) }}</button>
  <p>{{ t('common.welcomeUser', { userName: 'Alice' }) }}</p>
</template>
```

切换语种：

```ts
async function switchLocale(locale: string) {
  await lingua.syncLocale(locale);
  i18n.global.locale.value = locale;
}
```

## 4. 首屏 0ms：内置本地兜底（可选）

`syncLocale` 需要网络往返。想让首屏立即有文案、断网也能显示，用 CLI 把线上版本拉进仓库，再作为 `staticBundles` 传入：

```bash
npx lingua-sync pull          # 写入 src/locales/{culture}.json 和 lingua.lock.json（见 cli.md）
```

```ts
const modules = import.meta.glob('./locales/*.json', { eager: true, import: 'default' });
const staticBundles = Object.fromEntries(
  Object.entries(modules)
    .filter(([path]) => !path.endsWith('lingua.lock.json'))
    .map(([path, bundle]) => [path.match(/([^/]+)\.json$/)![1], bundle as Record<string, unknown>]),
);

setupLinguaVue(i18n, { endpoint, project: 'shop-vue', staticBundles });
```

加载顺序：静态包 → `localStorage` 缓存 → 网络（带 `If-None-Match`，未变化是 304）。

## 5. 热更新

`lingua.startHotUpdate()` 订阅 `/api/v1/stream`（SSE，断线按 1s→30s 指数退避重连），并每 60 秒轮询一次 manifest 兜底。
manifest 版本变化时，**已加载**的语种会整体替换（被删除的词条也会消失）。返回停止函数。

```ts
const stop = lingua.startHotUpdate();           // HMR / 卸载时调用 stop()
// 选项：pollIntervalMs（默认 60000，0 = 不轮询）、enableServerEvents（默认 true）、
//       onUpdated({ version, cultures })
```

## 6. 缺失词条上报（已移除）与 CI 检查

Vue 端**不做**缺词上报：密钥无法安全保管在浏览器端，且服务端只接受携带项目写密钥（`X-Lingua-Key`）的上报。

前端漏翻与「代码里用了但词条库没有」靠 `lingua-sync check` 在 CI 里发现（见 [cli.md](cli.md)）：
它扫源码里的 `t('…')` 与平台词条做双向比对，并校验占位符一致性与术语遵循。
平台的完成度门禁是发布时把最后一道关，与 CI 检查是两条独立的线，不重复。

## 配置项速查

| 选项 | 默认 | 说明 |
|---|---|---|
| `endpoint` | 必填 | Lingua 服务地址（末尾 `/` 会被去掉） |
| `project` | 必填 | 项目编码 |
| `staticBundles` | — | 打包内置的 `{culture: 消息}` |
| `pollIntervalMs` | `60000` | 热更新轮询间隔（仅在 `startHotUpdate()` 之后生效，`0` = 不轮询） |
| `enableServerEvents` | `true` | 热更新是否订阅 SSE（同样仅在 `startHotUpdate()` 之后生效） |
| `reportAdoption` | `true` | 请求头带 `X-Lingua-Client` / `X-Lingua-Version`，用于平台「采用情况」 |
| `clientName` | `vue` | 客户端类型标识（拼进 `X-Lingua-Client` 的 `kind` 段） |
| `onUpdated` | — | 热更新完成回调 |

返回值：`{ syncLocale(culture, replace?), refresh(), startHotUpdate() }`。

- `syncLocale(culture, replace)`：默认 `replace = false`，把静态包 / `localStorage` 缓存 / 网络结果**合并**进该语种；
  热更新路径传 `true`，整体替换（被删除的词条也会消失）。
- `refresh()`：拉一次 manifest，版本变了就重拉**已加载过**的语种。首次拿到版本号时只记录、返回 `false`
  （首屏内容在 `syncLocale` 时已经是最新的）。

## 限制

- 没有「列出平台已发布语种」的封装。需要做语言下拉框时，自己请求
  `GET {endpoint}/api/v1/projects/{project}/manifest`，读 `defaultCulture` 和 `cultures[].culture`。
- 前端没有「平台默认语种」的自动设置，`locale` / `fallbackLocale` 由应用自己定。
- 不做缺词上报（见第 6 节），也不自动设置 `fallbackLocale`——忘了配就看不到源语种回退。

## 验证过什么

在一个独立目录里 `npm i vue vue-i18n`，用 Node 22 直接运行 `setupLinguaVue` 对着本地 Lingua 服务：

- `syncLocale` 三个语种（zh-CN / en-US / ja-JP）都返回 `true`；
- 切到 `ja-JP` 后 `t('order.submitBtn', { count: 3 })` 得到 `チェックアウト (3)`，`t('common.welcomeUser', { userName: 'Alice' })` 得到 `お帰りなさい、Aliceさん！`；
- `refresh()` 在版本未变时返回 `false`；
- 取不存在的 key 时返回 key 本身；
- 热更新：`startHotUpdate()`（Node 的 `EventSource`）连上 SSE 后，在平台导入并发布，几秒内触发 `onUpdated({ version, cultures: ['ja-JP'] })`，内存里的文案随之替换。

**没有验证**：浏览器里的 `import.meta.glob` 写法（第 4 节）、浏览器中的 `localStorage` 缓存。

> 提示：导入与手工编辑的译文一律按「已审核」入库；AI 译文若违反术语库或占位符规则会进「待复核」，发布时该语种这条词条会回退成源语种文案——
> 热更新生效了但看到的是中文，先去平台看词条状态。
