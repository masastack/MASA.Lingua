# Documentation

English guides first, Chinese translations alongside (`.zh-CN.md`). The per-client integration guides under [integration/](integration/README.md) are maintained in Chinese.

| Document | 中文 | Content |
|---|---|---|
| [Quick start](quickstart.md) | [快速开始](quickstart.zh-CN.md) | Empty database → project → import → publish → consume, in 5 minutes |
| [Architecture](architecture.md) | [架构](architecture.zh-CN.md) | Components, projects, data flow, distribution contract, validation pipeline |
| [Deployment](deployment.md) | [部署](deployment.zh-CN.md) | Production checklist: image, volumes, HTTPS, GC, access model, CI/CD topology |

## UI reference (中文)

| Document | Content |
|---|---|
| [Masa.Blazor Utility Classes](ui/masa-blazor-utilities.zh-CN.md) | 管理后台样式唯一参考：Masa 实际提供的工具类、精确声明、响应式写法、皮肤兼容分级（A/B/C），以及 Masa **不提供**因而禁止书写的族。机器可读索引：[`ui/masa-blazor-utilities.json`](ui/masa-blazor-utilities.json)，由 `build/extract-masa-utilities.mjs` 从 `masa-blazor.min.css` 提取，组件库升级后重跑 |

## Client integration guides (中文)

| Client | Guide | Package / tool |
|---|---|---|
| Vue 3 + vue-i18n | [integration/vue.md](integration/vue.md) | `@lingua/vue` |
| ASP.NET Core (MVC / Minimal API / Razor Pages) | [integration/aspnetcore.md](integration/aspnetcore.md) | `MASA.Lingua.SDK.AspNetCore` |
| MASA Framework I18n (`I18n.T`) | [integration/aspnetcore.md](integration/aspnetcore.md#masa-framework-i18n-桥接) | `MASA.Lingua.SDK.MasaI18n` |
| Masa.Blazor frontend | [integration/aspnetcore.md](integration/aspnetcore.md#masa-blazor-接入) | `MASA.Lingua.SDK.MasaBlazor` |
| .NET MAUI / Blazor Hybrid / WPF | [integration/maui.md](integration/maui.md) | `MASA.Lingua.SDK.Maui` |
| Build time / CI | [integration/cli.md](integration/cli.md) | `lingua-sync` CLI |

---

# 文档

英文指南在前，中文翻译并行（`.zh-CN.md`）。[integration/](integration/README.md) 下的各端接入指南以中文维护。

| 文档 | English | 内容 |
|---|---|---|
| [快速开始](quickstart.zh-CN.md) | [Quick start](quickstart.md) | 空库 → 建项目 → 导入 → 发布 → 消费，5 分钟 |
| [架构](architecture.zh-CN.md) | [Architecture](architecture.md) | 组件、工程、数据流、分发契约、校验流水线 |
| [部署](deployment.zh-CN.md) | [Deployment](deployment.md) | 生产清单：镜像、挂卷、HTTPS、GC、访问模型、CI/CD 拓扑 |
| [Masa.Blazor 工具类](ui/masa-blazor-utilities.zh-CN.md) | — | 管理后台样式唯一参考：Masa 实际提供的工具类与精确声明、响应式写法、皮肤兼容分级、禁止书写的族 |

## 各端接入指南（中文）

| 客户端 | 指南 | 包 / 工具 |
|---|---|---|
| Vue 3 + vue-i18n | [integration/vue.md](integration/vue.md) | `@lingua/vue` |
| ASP.NET Core（MVC / Minimal API / Razor Pages） | [integration/aspnetcore.md](integration/aspnetcore.md) | `MASA.Lingua.SDK.AspNetCore` |
| MASA Framework I18n（`I18n.T`） | [integration/aspnetcore.md](integration/aspnetcore.md#masa-framework-i18n-桥接) | `MASA.Lingua.SDK.MasaI18n` |
| Masa.Blazor 前端 | [integration/aspnetcore.md](integration/aspnetcore.md#masa-blazor-接入) | `MASA.Lingua.SDK.MasaBlazor` |
| .NET MAUI / Blazor Hybrid / WPF | [integration/maui.md](integration/maui.md) | `MASA.Lingua.SDK.Maui` |
| 构建期 / CI | [integration/cli.md](integration/cli.md) | `lingua-sync` CLI |
