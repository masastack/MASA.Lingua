# 接入 ASP.NET Core

> English summary: integrating Lingua into ASP.NET Core (Minimal API / MVC / Razor Pages / Blazor Server) via `MASA.Lingua.SDK.AspNetCore` — a thin `IStringLocalizer` adapter with SSE + polling hot reload. Chinese guide below; a 5-minute end-to-end walkthrough lives in [docs/quickstart.md](../quickstart.md).

适用：.NET 8 / .NET 10 的 ASP.NET Core（Minimal API / MVC / Razor Pages / Blazor Server）。

业务代码保持标准的 `IStringLocalizer` 写法，Lingua 在后台同步 `{culture}.json` 到内存和本地磁盘。

## 前置条件

- Lingua 里已有这个项目并**发布过**；知道项目编码（例如内置示例 `auth-web-staging`）和服务地址。
- 读发布产物不需要密钥（本 SDK 只做读取与缺词上报）；密钥只用于缺词上报（项目写密钥 `X-Lingua-Key`，仅服务端 SDK 支持，见下文「限制」）。
  脚本化的创建 / 导入 / 导出默认匿名，服务端配置了共享写密钥 `Lingua:Distribution:WriteApiKey` 时才要求带密钥，见 [README 的访问策略](README.md)。

## 1. 引用

从 nuget.org 安装。版本号以 nuget.org 上的实际发布号为准（当前 `0.0.1-preview.1`）：

```xml
<ItemGroup>
  <PackageReference Include="MASA.Lingua.SDK.AspNetCore" Version="0.0.1-preview.1" />
</ItemGroup>
```

如果项目用中央包管理（`Directory.Packages.props`），版本写在那边，csproj 里只留包名：

```xml
<!-- Directory.Packages.props -->
<ItemGroup>
  <PackageVersion Include="MASA.Lingua.SDK.AspNetCore" Version="0.0.1-preview.1" />
</ItemGroup>

<!-- 消费方 csproj -->
<ItemGroup>
  <PackageReference Include="MASA.Lingua.SDK.AspNetCore" />
</ItemGroup>
```

nuget.org 上已公开发布，默认源即可安装。升级时只改版本号，
不要混用不同版本（`MasaI18n` / `MasaBlazor` / `Maui` 三个包依赖其余包的具体版本）：

```bash
dotnet restore
```

## 2. 注册

```csharp
using System.Globalization;
using MASA.Lingua.SDK.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLinguaLocalization(o =>
{
    o.ProjectCode = "auth-web-staging";             // 必填
    o.ServerEndpoint = "https://lingua.example.com";  // 不填则只读本地 i18n/*.json（离线模式）
    o.EnableMissingKeyTelemetry = builder.Environment.IsDevelopment();
    o.WriteKey = builder.Configuration["Lingua:WriteKey"]; // 缺词上报鉴权密钥（管理后台「接入密钥」面板获取）
});

var app = builder.Build();

// 语种由 ASP.NET 自己的请求本地化中间件决定：这里列出你的站点支持的语种（见「限制」）
var cultures = new[] { "zh-CN", "en-US", "ja-JP" };
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("zh-CN")
    .AddSupportedCultures(cultures)
    .AddSupportedUICultures(cultures));

app.MapGet("/", (IStringLocalizer L) => new
{
    userMissing = L["errors.UserNotExist"].Value,
    welcome     = L["common:welcomeUser", new { userName = "Alice" }].Value,
});

app.Run();
```

`UseRequestLocalization` 默认按 `Accept-Language` / Cookie / 查询串 `?culture=` 选择当前语种。

### 注入方式

```csharp
// 非泛型：key 原样使用
public class CartController(IStringLocalizer L) : Controller { /* L["order.submitBtn", …] */ }

// 泛型：短 key 自动加前缀（见下）
public class OrderController(IStringLocalizer<OrderController> L) : Controller { /* L["submitBtn"] → order.submitBtn */ }
```

`IStringLocalizer<T>` 会给不含 `.` / `:` 的短 key 自动加前缀：类型名去掉 `Controller` / `Page` 后转小写
（`IStringLocalizer<OrderController>` 里 `L["submitBtn"]` → 平台 key `order.submitBtn`）。
包含 `.` 或 `:` 的 key 原样使用。不想用前缀就注入非泛型的 `IStringLocalizer`。

## 3. 选项

`LinguaClientOptions`（**没有**语种列表、默认语种、API Key 这些选项，它们由平台决定）：

| 选项 | 默认 | 说明 |
|---|---|---|
| `ProjectCode` | 必填 | 项目编码 |
| `ServerEndpoint` | `null` | Lingua 服务地址。不填 = 离线，只用本地文件 |
| `LocalJsonDirectory` | `"i18n"` | 本地语言包目录（相对**进程工作目录**）。同步后会原子写入 `{culture}.json`，启动时先从这里加载 |
| `PollInterval` | 60 秒 | SSE **未连接**时的空闲对账周期（即兜底）；`TimeSpan.Zero` 关闭兜底 |
| `EnableServerEvents` | `true` | 订阅 SSE，发布后秒级同步；连着时空闲对账自动降到 5 分钟 |
| `EnableMissingKeyTelemetry` | `true` | 缺失词条上报，**建议只在开发环境开** |
| `WriteKey` | `null` | 项目写密钥（`X-Lingua-Key` 头，管理后台「接入密钥」面板获取）。开启上报后**必配**：未配置时上报器直接不发请求，只在整个进程里记一条 Warning（不会把 401 刷满日志）；配上密钥后无需重启即生效 |

> 需要「启动后是否真的可用」的运行时证据时，加 `AddLinguaDiagnostics()` + `app.MapLinguaStatus()`，见下文「诊断与自检」。

## 4. 它是怎么工作的

1. **启动**：先加载 `LocalJsonDirectory` 里已有的 `{culture}.json`（服务端不可达也能起来），并监听该目录的文件变化。
2. **同步**：请求 manifest（带 `If-None-Match`，没变是 304）→ 版本变了就只拉 ETag 变化的语种 → 原子写盘 + 替换内存字典。
   **平台已发布的全部语种都会同步**，默认语种取 manifest 的 `defaultCulture`，作为词条缺失时的回退。
   - **本地文件被删会自动补回**：只要某个语种的 `{culture}.json` 丢了（被清理 / 误删），下一次对账就不带 `If-None-Match`、
     重新拉全量并补盘，不会被 304 永远拦住。平台已移除的语种不参与这个判断（本地文件保留作离线兜底）。
   - **语种名必须是安全文件名**：manifest / SSE 事件里的语种名会拼进本地路径，只接受字母 / 数字 / `-` / `_`（≤ 35 字符）；
     含 `/`、`\`、`..` 等的条目会被跳过并记一条 Warning，不会把文件写到 `LocalJsonDirectory` 之外。
3. **一个同步循环**：SSE 事件与「队列空闲超时」都投递到同一个队列，串行执行、自动合并。
   - SSE 事件带版本指纹与各语种 ETag：客户端据此直接拉变化的语种，**省掉一次 manifest 往返**（实时路径）。
   - 队列静默满一个周期就主动对账一次（兜底路径）。SSE 连着时周期是 5 分钟；断开或未启用时回到
     `PollInterval`（60 秒）——那是代理/防火墙掐长连接时唯一能让客户端追上线上版本的路径。
   - 同步失败按 1s→60s 退避重试，期间继续用本地文件。
4. **取词**：从内存里的 `FrozenDictionary` 无锁读取。找不到时依次回退：同语种 → 中性语种（`zh-CN`→`zh`）→ 平台默认语种；都没有则返回 key 本身，`ResourceNotFound == true`。

> `EnableServerEvents = false` 且 `PollInterval = 0` 时，启动后不会再感知任何发布（启动日志会给出警告）。

> 容器 / 多实例部署：`LocalJsonDirectory` 要对进程可写。只读文件系统下指到一个可写目录（例如 `/tmp/i18n`）。

## 诊断与自检

`AddLinguaDiagnostics()` 提供一套与业务无关的运行状态：只读 SDK 自己的内存字典、同步服务与配置，
**不含任何项目、语种或词条级硬编码**，普通 ASP.NET Core 宿主与 `AddLinguaMasaI18n` 宿主都能直接用。

```csharp
builder.Services.AddLinguaLocalization(...);   // 或 AddLinguaMasaI18n(...)
builder.Services.AddLinguaDiagnostics();       // 必须在上面两者之后，重复调用幂等

var app = builder.Build();
app.MapLinguaStatus().AllowAnonymous();        // 默认路径 /api/lingua/status，鉴权策略由宿主决定
```

- **启动结论日志**（最多等 20 秒）：`词条已就绪（版本 / SSE / 默认语种 / 各语种词条数）`、
  `离线模式（未配置 ServerEndpoint）`、`20 秒内未完成同步（附项目编码与服务地址）`。
  多语言挂掉时不必等用户看到界面全是 key 才发现。
- **状态端点**：

```jsonc
{
  "projectCode": "auth-web-staging",
  "serverEndpoint": "http://localhost:62641",
  "mode": "online",                 // offline = 未配置地址，只用本地 {culture}.json
  "version": "3f2a…",               // null = 还没同步成功
  "serverEventsConnected": true,
  "defaultCulture": "zh-CN",
  "cultures": [ { "culture": "zh-CN", "entries": 101 } ],
  "healthy": true,                  // 至少一个语种加载到了词条
  "checkedAt": "2026-10-31T10:00:00+08:00"
}
```

`MapLinguaStatus()` 返回 `IEndpointConventionBuilder`：需要鉴权就链 `.RequireAuthorization()`，需要换路径传第一个参数。

## MASA Framework I18n 桥接

适用：MASA 后端服务（`I18n.T` / `II18n<T>` / UserFriendlyException 错误码 / FluentValidation 的 `MasaLanguageManager`）。
换的是**词条来源**，业务调用点一行都不用改。

```xml
<PackageReference Include="MASA.Lingua.SDK.MasaI18n" Version="0.0.1-preview.1" />
```

`MasaI18n` 包本身依赖 `MASA.Lingua.SDK.AspNetCore` 与 `MASA.Lingua.Core`，所以只需引用这一个包；
但如果同一工程还直接用了 `LinguaClientOptions` / `AddLinguaDiagnostics()` 这类 AspNetCore 包的类型，
建议把 `MASA.Lingua.SDK.AspNetCore` 也显式写上同一个版本，避免依赖传递版本与直接引用版本不一致。

```csharp
using MASA.Lingua.SDK.AspNetCore;

// 普通 ASP.NET Core 宿主（没有引入 MASA.Contrib.Configuration）这样读配置：
builder.Services.AddLinguaMasaI18n(o =>
{
    builder.Configuration.GetSection("Lingua").Bind(o);
    o.ProjectCode = "auth-web-staging";             // 必填
    o.ServerEndpoint = "https://lingua.example.com";  // 不填则只读本地 i18n/*.json
});

// MASA Stack 宿主（调过 AddMasaStackConfigAsync）必须改读 Masa 配置根，见下方警告
// builder.Services.AddLinguaMasaI18n(o =>
//     builder.Services.GetMasaConfiguration().ConfigurationApi.GetDefault().GetSection("Lingua").Bind(o));

// 替换宿主里的 app.UseI18n()：默认语种由平台项目的 DefaultCulture 决定，代码里不写死
app.UseLinguaI18n();
```

> **默认语种由平台决定，不需要在宿主配置。** MASA 的 `UseI18n()` 在启动时一次性构造请求本地化选项，没传
> `defaultCulture` 就取语种列表第一个 —— 而 Lingua 的语种列表来自本地 `{culture}.json` 的文件枚举顺序，
> 那一刻 manifest 也还没拉回来。`UseLinguaI18n()` 改为在<b>请求时</b>读平台同步下来的默认语种并挂到
> provider 链末尾，所以平台改默认语种并发布后，下一次请求即生效（无需重启）。
> 优先级不变：`?culture=` > 语言 Cookie > `Accept-Language` > 平台默认语种。

```jsonc
// appsettings.json
"Lingua": {
  "ProjectCode": "auth-web-staging",
  "ServerEndpoint": "https://lingua.example.com",
  "LocalJsonDirectory": "i18n",
  "EnableMissingKeyTelemetry": false,   // 生产必须关
  "WriteKey": ""                        // 缺词上报鉴权密钥，开发环境需要上报时配置（管理后台「接入密钥」面板获取）
}
```

> ⚠️ **MASA Stack 宿主读配置要小心**：如果宿主调了 `AddMasaStackConfigAsync`（或任何 `AddMasaConfiguration`），
> `appsettings.json` 会被迁到 Masa 的配置根下，根 `builder.Configuration` 读不到原来的段
> （`Masa.Contrib.Configuration` 内部会 `Sources.Clear()`）。这时必须从 Masa 配置根读，例如 MASA.Auth 两个宿主的写法：
> `builder.Services.GetMasaConfiguration().ConfigurationApi.GetDefault().GetSection("Lingua").Bind(options)`
> （Admin 服务用的是 `masaConfiguration.ConfigurationApi.GetDefault()`，同一份数据）。
> 用 `builder.Configuration.GetSection("Lingua")` 会得到空段；`ProjectCode` 缺失会启动即抛异常（当前版本不再静默兜底成 `default`）。
> 环境变量注入（`Lingua__ProjectCode`）走根配置，不受影响。
> 具体取值以宿主引入的 MASA 版本为准；判据很简单——**启动日志里 `/api/lingua/status` 的 `mode` 是 `online` 就说明读到了**。

要点：

1. **不要同时注册 `AddLinguaLocalization`**。`AddLinguaMasaI18n` 内部已经调用它；再注册一次会启动两个同步循环、两条 SSE 连接
   （`LinguaJsonSyncService` 用的是 `Add` 而不是 `TryAdd`）。
2. **`LocalJsonDirectory` 的相对路径基准是 `AppContext.BaseDirectory`**（不是进程工作目录），与 `AddLinguaLocalization` 单独使用时不同。
   容器 / Windows 服务部署时这是好事，但别按 CWD 去推算路径。
3. **本地 JSON 是真正的兜底层**：桥接在注册时把发布的 `{culture}.json` 冻结为只读兜底 contributor，
   并把同步产物写到 `LocalJsonDirectory/.lingua-cache/`——发布的兜底文件永不被覆盖，平台没有的 key
   始终（包括重启后）能从本地取到；
   而 `AddLinguaLocalization` 单独使用时，首次同步会用平台内容整语种覆盖本地文件与字典。
   所以 `Assets/I18n/*.json` 要随程序集输出（`CopyToOutputDirectory`），并拷进输出目录的 `i18n/`。
   **key 的形态必须与平台发布产物一致**：如果业务调用是平铺 key（`I18n.T("UserNotExist")`），
   平台里这些词条就要放在**项目根命名空间**，发布成扁平根 key；放进命名空间会发布成嵌套对象，
   平铺 key 从平台取不到，只能落到本地兜底。
4. **支持哪些语种完全由平台决定**，本地文件不参与协商。同步服务把 manifest 的 `cultures` 写进
   `ILinguaMemoryStore.SupportedCultures`，`UseLinguaI18n()` 在请求管道里用它覆盖
   `RequestLocalizationOptions.SupportedCultures/SupportedUICultures`。所以**平台新增语种并发布后，客户端无需改配置、无需放本地文件、无需重启**。
   本地 `{culture}.json` 只在启动阶段用于铺好 contributor 表（离线可启动、首屏不全是 key），
   以及平台不可达时作为兜底内容 —— 删掉它们不会影响多语种切换，只是失去离线能力。
5. **本地 `i18n/` 目录只加载文件名是 BCP47 语种形状（`en` / `zh-CN` / `zh-Hans-CN` 这类）的 `*.json`**：
   `supportedCultures.json`、`lingua.lock.json` 之类的元数据 / 杂项文件不满足形状，天然被排除，不会被当成语种加载。

## Masa Blazor 接入

适用：Blazor（含 MFE 子应用）里直接用 Masa.Blazor 的 `T()` / `I18n`，希望词条由平台统一管理。

```xml
<PackageReference Include="MASA.Lingua.SDK.MasaBlazor" Version="0.0.1-preview.1" />
```

```csharp
using MASA.Lingua.SDK.MasaBlazor;

builder.Services.AddMasaBlazor(...).AddI18nForServer("wwwroot/i18n");   // 静态词条先加载，作离线兜底

builder.Services.AddLinguaForMasaBlazor(o =>
{
    builder.Configuration.GetSection("Lingua").Bind(o);
    o.ProjectCode = "auth-sso-staging";
    o.ServerEndpoint = "https://lingua.example.com";
});
```

`AddLinguaForMasaBlazor` 按宿主形态**自动二选一**，这是理解该包的关键：

| 模式 | 触发条件 | 行为 |
|---|---|---|
| **共享 store 订阅** | 宿主已注册 `ILinguaMemoryStore` + `ILinguaChangeNotifier`（即已调过 `AddLinguaMasaI18n` 或 `AddLinguaLocalization`） | 不再独立发 HTTP；启动时把共享 store 已载入的语种合并进 Masa.Blazor 的 I18n 缓存，并订阅语种更新事件。之后 `LinguaJsonSyncService` 每次同步某语种（SSE 发布 / 空闲对账 / 本地文件变更）都会自动重合并——与 MASA Framework I18n 词条共用同一条同步链 |
| **独立拉取** | 未注册 Lingua store（典型是 Blazor Wasm / 只接了 Masa.Blazor 的应用） | 注册 hosted service，启动时拉一次 manifest + 逐语种 bundle（整体限时 10 秒），展平成 `Namespace.key` 合并进 I18n 缓存。**不订阅 SSE、不轮询、不落盘**：平台发布后需要刷新页面才生效 |

两种模式共同的边界：

- 合并走反射调用 Masa.Blazor 内部的 `I18nCache.AddLocale(culture, map, isBuiltInLocale: false)`，让 Lingua 词条**优先**于
  `wwwroot/i18n/*.json` 的静态文案。反射不可用时降级为公开的 `I18n.AddLocale`——那条按「内置词条」合并，
  静态文件里的同名 key 会胜出，于是只有静态文件里没有的 key 生效。降级本身记 Debug，降级也失败才记 Warning。
- 该反射对齐仓库当前引用的 Masa.Blazor 版本（见 `src/SDK/MASA.Lingua.SDK.MasaBlazor/MASA.Lingua.SDK.MasaBlazor.csproj`）。
  升级 Masa.Blazor 主版本前请冒烟。
- 仓内**没有该包的自动化测试**；真实消费方是 MASA.Auth 的 SSO 门户。

> 认证门户这类关键页面，建议保留 `wwwroot/i18n` 静态文件作为唯一兜底，把 Lingua 当「可热改的上层」。

### 同时用 MasaBlazor 与 MasaI18n（双注册共存）

SSO 门户这类宿主常见组合：登录页 UI 用 Masa.Blazor 的 `T()`，服务端错误码走 MASA I18n。
两者**可以共存**，而且现在的推荐方式就是上面的「共享 store 订阅」——同一个 `ILinguaMemoryStore` 供两套查询链：

```csharp
// ① Blazor 组件里的 T()：Masa.Blazor 的 I18n 缓存（静态 wwwroot/i18n 先加载作兜底）
builder.Services.AddMasaBlazor(...).AddI18nForServer("wwwroot/i18n");

// ② 服务端错误码 / FluentValidation：MASA Framework I18n（Lingua 供词，本地 Assets/I18n 兜底）
builder.Services.AddLinguaMasaI18n(o => ...);
app.UseLinguaI18n();

// ③ UI 词条也从 Lingua 供：检测到 ② 注册的 store 后自动进入订阅模式，不再独立拉取
builder.Services.AddLinguaForMasaBlazor(o => ...);
```

注意：

- **两套本地兜底文件是两份**：`wwwroot/i18n`（Masa.Blazor，由 `supportedCultures.json` 决定加载哪些语种）与
  `Assets/I18n`（MASA I18n 的兜底目录）互不相同。一边缺语种、另一边正常，排查时容易误判。
- 只注册 `AddLinguaForMasaBlazor` 而不注册 `AddLinguaMasaI18n` 时走**独立拉取模式**：UI 能拿到 Lingua 词条，
  但服务端错误码仍在用 MASA I18n 自己的静态资源。

## 临时验证未发版的改动（例外情况）

正常接入**一律从包源安装**。只有需要在自己工程里验证一个还没发版的 SDK 改动时，才临时走本地包源：

1. 在 Lingua 仓库按当前版本号打包。**版本号只在 `Directory.Build.props` 维护一处**，不要用 `-p:PackageVersion`
   单独指定，否则依赖 `AspNetCore` 的 `MasaI18n` 会解析到另一个版本：

   ```bash
   dotnet pack src/MASA.Lingua.Core -c Release -o local-packages
   dotnet pack src/SDK/MASA.Lingua.SDK.Abstractions -c Release -o local-packages
   dotnet pack src/SDK/MASA.Lingua.SDK.AspNetCore -c Release -o local-packages
   dotnet pack src/SDK/MASA.Lingua.SDK.MasaI18n -c Release -o local-packages
   dotnet pack src/SDK/MASA.Lingua.SDK.MasaBlazor -c Release -o local-packages
   ```

   有 MAUI / 客户端应用消费方时，把 `src/SDK/MASA.Lingua.SDK.Maui` 也加进打包清单——它同样依赖
   `Abstractions`，版本必须与其余包一致（共 6 个包）。

2. 消费方仓库的 `NuGet.config` 加一个本地源，把 `*.nupkg` 放进去：

   ```xml
   <configuration>
     <packageSources>
       <add key="local-packages" value="./local-packages" />
     </packageSources>
   </configuration>
   ```

3. csproj 仍然是 `PackageReference` —— 只是 NuGet 从本地源解析，不要改成 `ProjectReference`：
   **`ProjectReference` 会绕过包的版本与依赖声明，还可能在一个进程里加载两份 SDK**（同步循环跑两遍）。
   **升版本时所有引用了 SDK 的 csproj 必须一起改**，否则会解析出两个不同版本的 `AspNetCore`。

4. 验证完把本地源去掉、改回正式版本，别把 `local-packages` 目录和本地源提交进业务仓库。

> NuGet 缓存会记住旧版本：改了 nupkg 却没升版本号时，`dotnet restore` 仍会用缓存里的旧包。
> 升版本号是最省事的做法；确实要复用版本号，得先 `dotnet nuget locals global-packages --clear`。

## 限制

- **`UseRequestLocalization` 的语种列表要自己写**：ASP.NET 在请求开始时就要知道站点支持哪些语种，Lingua 目前不会替你填这个列表。
  列表里没有的语种，请求会回落到默认语种；平台新增了语种，要在这里加上才会生效。
  （走 `AddLinguaMasaI18n` + `UseLinguaI18n()` 的宿主不受此限：语种列表由平台 manifest 覆盖。）
- 缺失词条上报需要项目写密钥（`Lingua:WriteKey` → `X-Lingua-Key` 头），服务端校验失败返回 401/403；
  **只有服务端 SDK 支持上报**（MAUI / Vue 已移除该能力）。仍建议在非开发环境关掉 `EnableMissingKeyTelemetry`，
  否则线上会把真实用户触发的缺失 key 写进平台。
  上报的 key 会成为平台里的待翻译词条，试验用的 key 记得到平台清理。
- 平台把译文标成「待复核」的词条，发布时该语种会回退成源语种文案。

## 排错

| 现象 | 看哪里 |
|---|---|
| 页面全是 key | 项目没发布（manifest 404）；或 `ServerEndpoint` 写错；看日志 `同步 … 语言包失败` |
| 日志 `已同步并热更新项目 …` 没出现 | 服务端不可达，或项目编码不对（manifest 404） |
| 切了 `Accept-Language` 没变化 | `UseRequestLocalization` 没把这个语种加进支持列表 |
| `i18n` 目录权限报错 | `LocalJsonDirectory` 不可写 |

## 验证过什么

新建 ASP.NET Core 8 Minimal API 工程，从 nuget.org 安装 `MASA.Lingua.SDK.AspNetCore`，按上面第 2 节写法对着本地 Lingua 服务运行：

- 启动后自动在工作目录生成 `i18n/zh-CN.json`、`en-US.json`、`ja-JP.json`（三个语种来自平台，客户端没有配置任何语种）；
- `Accept-Language: en-US / ja-JP / zh-CN` 分别返回对应语种，`L["common:welcomeUser", new { userName = "Alice" }]` 得到
  `Welcome back, Alice!` / `お帰りなさい、Aliceさん！` / `欢迎回来，Alice！`；
- 在平台导入并发布一条译文后，几秒内业务端拿到新版本（SSE 触发）；
- 请求不存在的 key：`ResourceNotFound == true`，开发环境下会触发缺失上报。

仓内另有针对性测试：`MasaI18nBridgeTests`（桥接取词 / 热更新 / 根命名空间平铺 key / 本地兜底）、
`LinguaDefaultCultureProviderTests`（平台默认语种的请求时覆盖）、`LinguaDiagnosticsTests`（状态端点字段）、
`ReporterWriteKeyTests`（写密钥头与缺密钥时不发请求）、`ProjectCultureSettingsTests`（平台语种覆盖协商列表）。

**没有验证**：反向代理 / 多实例部署（站点强制单实例，SSE 广播与发布缓存都是进程内状态）。
