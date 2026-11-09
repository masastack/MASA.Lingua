# 参与贡献 MASA.Lingua

感谢你有兴趣参与贡献！本文档涵盖开发环境、构建测试方式，以及提交 PR 时需要包含的内容。

[English](CONTRIBUTING.md)

## 开发环境

| 依赖 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 10.0 | 由 [global.json](global.json) 钉住；SDK 包目标 `net8.0;net10.0` |
| Node.js | >= 18 | 仅 `packages/lingua-vue`（适配器 + `lingua-sync` CLI）需要 |
| Docker | 任意近期版本 | 集成测试需要（[Testcontainers](https://dotnet.testcontainers.org/) 会拉起真实 PostgreSQL） |

## 构建与测试

```bash
# 构建整个解决方案
dotnet build -c Release

# 运行 .NET 测试套件（集成测试通过 Testcontainers 启动真实 PostgreSQL）
dotnet test -c Release

# 运行 lingua-vue 测试套件
cd packages/lingua-vue && npm install && npm test
```

测试使用 [Testcontainers.PostgreSql](https://www.nuget.org/packages/Testcontainers.PostgreSql)——不需要本地装数据库，但要求 Docker 守护进程可用。

## 项目结构

```
src/
  MASA.Lingua.Core/               # 领域模型 + 包编译器 + 校验（双目标 net8/net10）
  MASA.Lingua.AI/                 # AI 批量翻译（Microsoft.Extensions.AI）
  MASA.Lingua.Server/             # Blazor 管理后台 + 分发端点（net10）
  Storage/
    MASA.Lingua.Storage.EfCore/   # EF Core 仓储与种子数据
    MASA.Lingua.Storage.Migrations.PostgreSql/   # EF 迁移（仅 PostgreSQL）
  SDK/                            # 客户端包（双目标 net8/net10）
packages/lingua-vue/              # Vue 3 适配器 + lingua-sync CLI（Node）
tests/MASA.Lingua.Tests/          # xunit；服务端测试跑在真实 PostgreSQL 容器上
```

动手改 [MASA.Lingua.Core](src/MASA.Lingua.Core) 或任何 `SDK.*` 项目前值得知道的硬约束：它们只能使用 **net8 可用的 API**——net10 专属能力应放到 `Server` / `Storage.*` / `AI`。

## 代码风格

- `Nullable` 与 `ImplicitUsings` 已启用；`LangVersion` 为 `latest`（见 [Directory.Build.props](Directory.Build.props)）。
- 文件级命名空间、尽量 `sealed`、公共 API 写 XML doc 注释——本仓库刻意注释「为什么」而不只是「是什么」。请保持这种风格。
- 本仓库注释以中文书写；欢迎贡献者沿用，但不作强制要求。
- CI 会跑 `dotnet format --verify-no-changes`（当前为提示性检查）；推送前本地跑一次 `dotnet format` 能保持 diff 干净。

## Pull Request

1. Fork / 从 `main` 拉分支。
2. 行为变更要补充或调整测试——现有套件覆盖分发、校验、存储与 SDK 行为；修 bug 请附回归测试。
3. 改动公共行为（端点、配置键、SDK 契约）时同步更新 [docs/](docs) 下的文档。
4. 保持 PR 聚焦：一个 PR 只做一个逻辑变更。
5. CI 必须通过：`pr_run_test_ci.yml` 会构建解决方案、跑两套测试并检查格式。

## 发版（维护者）

1. 更新 [Directory.Build.props](Directory.Build.props) 中的 `Version`（6 个包共用这一个版本源）。
2. 更新 [CHANGELOG.md](CHANGELOG.md)。
3. 以裸 semver 打 tag（如 `1.3.1`，不带 `v` 前缀）并发布 GitHub Release。
4. `package_push_nuget.org.yml` 会打包全部包并经 NuGet OIDC 登录推送到 nuget.org。
5. 站点部署仍在 GitLab：`main` 的推送由 `mirror-gitlab.yml` 镜像，再由 `.gitlab-ci.yml` 部署。
