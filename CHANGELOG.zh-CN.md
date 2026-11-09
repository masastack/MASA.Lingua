# 更新日志

MASA.Lingua 的所有显著变更记录于此。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循 [SemVer](https://semver.org/)。

[English](CHANGELOG.md)

## [Unreleased]

## [0.0.1-preview.1]

MASA.Lingua 首个公开预览版，以 Apache License 2.0 开源。

### 新增

- Blazor 管理后台：词条矩阵、命名空间与语种管理、术语与导入导出工具。
- 确定性包编译器，产出嵌套/扁平 `{culture}.json` 与基于 SHA-256 的 ETag；发布产物跨服务重启按字节还原。
- 分发端点：强 ETag / `Cache-Control: no-cache` / 304 语义；SSE 发布流支持 `Last-Event-ID` 补发。
- 全局共享命名空间（`common` / `errors`）与项目命名空间分层合并、按层回退。
- 导入端点（`import-json`），支持 `publish=true` 立即发布，请求上限 10MB。
- 经 Microsoft.Extensions.AI 的 AI 批量翻译（任意 OpenAI 兼容端点），结果先过确定性校验。
- 术语强制：在词条编辑与发布两个关口校验，并给出合规报告。
- 发布门禁策略，明确输出 `publishBlockedReason`。
- 存储仅支持 PostgreSQL，提供独立 EF 迁移包（`MASA.Lingua.Storage.Migrations.PostgreSql`）。
- 缺词上报：要求携带项目写密钥（`X-Lingua-Key`）；仅服务端 SDK 支持上报。
- `lingua-sync` CLI（`pull` / `push` / `check`）：构建期拉取产物、批量导入与 CI 检查；npm 上的 `@lingua/vue` Vue 3 适配器。
- 共用同一版本号的 6 个 NuGet 包：
  - `MASA.Lingua.Core`——轻量 i18n 核心领域、包编译器、JSON 转换器与确定性校验引擎。
  - `MASA.Lingua.SDK.Abstractions`——SDK 客户端抽象。
  - `MASA.Lingua.SDK.AspNetCore`——ASP.NET Core `IStringLocalizer` 适配器：本地 `{culture}.json` 缓存、FrozenDictionary 查询、文件监听与 ETag/SSE 热更新（轮询兜底）。
  - `MASA.Lingua.SDK.MasaI18n`——MASA Framework I18n（`II18n` / `I18n.T`）桥接；同步产物写入 `{LocalJsonDirectory}/.lingua-cache/`，随包发布的 `{culture}.json` 保持原样、作为冻结兜底。
  - `MASA.Lingua.SDK.MasaBlazor`——Blazor（Server/Wasm）+ Masa.Blazor 语言资源适配器：宿主注册了共享 store 时走合并与热更新，否则启动时拉取发布产物；静态 `wwwroot/i18n` JSON 始终作为离线兜底。
  - `MASA.Lingua.SDK.Maui`——.NET MAUI / Blazor Hybrid / WPF 客户端 Localizer，启动与回到前台时同步；不依赖 MAUI workload，任意 .NET 8/10 客户端应用均可引用。
