# 快速开始

> [English](quickstart.md) · [返回文档目录](README.md)

从空库到一条已发布、已被客户端消费的译文——大约 5 分钟。管理后台需要 SSO，但本文所有步骤都走匿名机器 API，无需配置 SSO。

## 1. 启动 PostgreSQL 与服务端

```bash
# PostgreSQL（唯一支持的数据库）
docker run -d --name lingua-pg \
  -e POSTGRES_PASSWORD=lingua -e POSTGRES_DB=lingua \
  -p 5432:5432 postgres:16
```

```bash
# Lingua 服务端（仓库内本地运行；需要 .NET 10 SDK）
dotnet run --project src/MASA.Lingua.Server -- \
  Lingua__Database__ConnectionString="Server=localhost;Port=5432;User Id=postgres;Password=lingua;Database=lingua;" \
  Auth__Authority=https://sso.invalid \
  Auth__ClientId=local
```

`Auth__Authority` 覆盖会跳过 MASA Stack 的 DCC/SSO 解析，进程可以在没有 MASA Stack 部署的环境启动。管理后台登录不会生效（覆盖分支背后没有真实 SSO）——没关系，下面用的机器 API 是匿名的。

HTTP 端口来自 [launchSettings.json](../src/MASA.Lingua.Server/Properties/launchSettings.json)：`http://localhost:62641`。

> 空库首次启动会自动播种三个示例项目（`auth-web-staging`、`auth-sso-staging`、`shop-vue`）。

## 2. 建项目

```bash
curl -X POST http://localhost:62641/api/v1/projects/demo
# {"project":"demo","defaultCulture":"zh-CN","created":true,"writeKey":"..."}
```

- 如果服务端配置了 `Lingua:Distribution:WriteApiKey`，建项目、导入、删除都要加请求头 `-H "X-Lingua-Key: <该密钥>"`（没带 `401`、不匹配 `403`）。它是部署级的共享密钥，不是下面每个项目各自的 `writeKey`。
- 项目编码只允许 ASCII 字母、数字、`-`、`_`（1–64 位），其余一律 `400`。
- 幂等：项目已存在时返回 `200` 且 `created: false`，不改动任何配置。
- `writeKey`（仅新建时返回）用于缺词上报鉴权；打算用服务端 SDK 上报就先存下来。
- 默认语种：默认 `zh-CN`，支持 `zh-CN` / `en-US` / `ja-JP`。语种在导入时按需自动补齐。

## 3. 导入并发布

```bash
curl -X POST "http://localhost:62641/api/v1/projects/demo/import-json?culture=en-US&publish=true" \
  -H "Content-Type: application/json" \
  -d '{
    "greeting": "Hello, {name}!",
    "errors.UserNotFound": "User {userId} does not exist."
  }'
```

- `publish=true` 导入后立即发布；不传则只落草稿。
- `defaultNs` 不传或留空 = **项目根命名空间**——顶层扁平 key 发布为 JSON 根级裸 key。
- 占位符是命名的（`{name}`）或位置参数（`{0}`）。
- 请求体上限 10 MB（超限返回 `413`）。

验证发布产物：

```bash
curl -i http://localhost:62641/i18n/demo/en-US.json
# HTTP 200 带强 ETag；带上 If-None-Match 重发 -> 304

curl http://localhost:62641/api/v1/projects/demo/manifest
# 版本指纹 + 已发布语种 + 默认语种
```

## 4. 客户端消费

### ASP.NET Core（net8.0 / net10.0）

```bash
dotnet add package MASA.Lingua.SDK.AspNetCore
```

```csharp
builder.Services.AddLinguaLocalization(options =>
{
    options.ServerEndpoint = "http://localhost:62641";
    options.ProjectCode = "demo";
});
```

然后按标准 `IStringLocalizer` 写法取词：

```csharp
L["greeting", new { name = "MASA" }]   // "Hello, MASA!"
L["errors:UserNotFound", new { userId = 42 }]
```

`:` 与 `.` 分隔符等价。SDK 把 `{culture}.json` 同步进内存与本地目录，SSE 发布事件触发热更新，另有固定间隔轮询兜底。

### Vue 3 + vue-i18n

```bash
npm i @lingua/vue vue-i18n
```

```ts
import { createLingua, startHotUpdate } from '@lingua/vue';
// 完整初始化见：docs/integration/vue.md
```

### 其他客户端

| 客户端 | 指南 |
|---|---|
| MASA Framework `I18n.T` | [docs/integration/aspnetcore.md](integration/aspnetcore.md#masa-framework-i18n-桥接) |
| Masa.Blazor `T()` | [docs/integration/aspnetcore.md](integration/aspnetcore.md#masa-blazor-接入) |
| .NET MAUI / WPF | [docs/integration/maui.md](integration/maui.md) |
| CI 检查 / 批量导入 | [docs/integration/cli.md](integration/cli.md) |

## 5. 体验管理后台（需要真实 MASA Stack SSO）

有真实 MASA Stack 时，去掉 `Auth__Authority` 覆盖，配置 `DCC` 段（或让 `MasaStackConfig` 自动解析），在 MASA Auth 注册 OIDC 客户端，打开 `https://<host>/` 即可——词条矩阵、AI 翻译与术语页面都在那里。见 [docs/deployment.zh-CN.md](deployment.zh-CN.md)。

## 下一步

- [架构](architecture.zh-CN.md)——产物如何编译、发布与分发
- [部署](deployment.zh-CN.md)——生产清单（HTTPS、持久化、单实例约束）
- [接入指南](integration/README.md)——各端 SDK 细节与经过验证的示例
