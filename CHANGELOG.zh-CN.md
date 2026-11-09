# 更新日志

MASA.Lingua 的所有显著变更记录于此。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循 [SemVer](https://semver.org/)。

[English](CHANGELOG.md)

## [Unreleased]

- 仓库以 Apache License 2.0 正式开源。
- NuGet 包发布从内部 CI 迁移到 GitHub Actions（`package_push_nuget.org.yml`，由 GitHub Release 触发）。
- 站点部署保留在内部 GitLab，由 GitHub→GitLab 镜像工作流馈送。

## [1.3.0]

### 新增

- `MASA.Lingua.SDK.Maui`——.NET MAUI / Blazor Hybrid / WPF 客户端的 Localizer，启动与回到前台时同步。
- 项目**写密钥**：缺词上报端点要求携带项目写密钥（`X-Lingua-Key`）；仅服务端 SDK 支持上报。
- 独立的 PostgreSQL 迁移包（`MASA.Lingua.Storage.Migrations.PostgreSql`）。

### 变更

- 存储仅支持 PostgreSQL，移除 SQLite 路径。
- 发布产物与 bundle ETag 完全确定性——服务重启后从数据库快照按字节还原线上版本。

## [1.2.0]

### 新增

- `lingua-sync` CLI（`pull` / `push` / `check`）：构建期拉取产物、批量导入与 CI 检查。
- 导入端点（`import-json`），支持 `publish=true` 立即发布，请求上限 10MB。
- 全局共享命名空间（`common` / `errors`）与项目命名空间分层合并、按层回退。

## [1.1.0]

### 新增

- 经 Microsoft.Extensions.AI 的 AI 批量翻译（任意 OpenAI 兼容端点），结果先过确定性校验。
- 术语强制：强制术语在词条编辑与发布两个关口校验，并给出合规报告。
- 发布门禁策略，明确输出 `publishBlockedReason`。

## [1.0.0]

### 新增

- 首个版本：Blazor 管理后台、词条矩阵、命名空间与语种管理。
- 确定性包编译器，产出嵌套/扁平 `{culture}.json` 与基于 SHA-256 的 ETag。
- 分发端点：强 ETag / `Cache-Control: no-cache` / 304 语义。
- SSE 发布流，支持 `Last-Event-ID` 补发。
- `MASA.Lingua.SDK.AspNetCore`（`IStringLocalizer` 适配器，SSE + 轮询热更新）与 `MASA.Lingua.SDK.MasaI18n`（MASA Framework 桥接）。
