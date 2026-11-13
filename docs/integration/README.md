# MASA.Lingua 接入文档

> English overview: pick a guide by client type — Vue 3 (`@lingua/vue`), ASP.NET Core (`MASA.Lingua.SDK.AspNetCore`), MASA Framework I18n / Masa.Blazor (`MASA.Lingua.SDK.MasaI18n` / `MASA.Lingua.SDK.MasaBlazor`), .NET MAUI (`MASA.Lingua.SDK.Maui`), or the build-time `lingua-sync` CLI. See also [docs/README.md](../README.md) for the bilingual index. Chinese guide below.

业务项目怎么接入 Lingua，按项目类型选一份：

| 项目类型 | 文档 | 包 / 工具 | 热更新 |
|---|---|---|---|
| Vue 3 + vue-i18n | [vue.md](vue.md) | `@lingua/vue` | SSE + 轮询，`startHotUpdate()` |
| ASP.NET Core（MVC / Minimal API / Razor Pages） | [aspnetcore.md](aspnetcore.md) | `MASA.Lingua.SDK.AspNetCore` | SSE + 轮询兜底，自动 |
| MASA 后端服务（MASA Framework I18n：`I18n.T` / `II18n<T>`） | [aspnetcore.md](aspnetcore.md#masa-framework-i18n-桥接) | `MASA.Lingua.SDK.MasaI18n`（`AddLinguaMasaI18n` + `UseLinguaI18n`） | 发布后错误码等实时热更新，调用点零改动；默认语种跟随平台 |
| MASA Blazor 前端（Masa.Blazor `T()`，如 MFE 子应用） | [aspnetcore.md](aspnetcore.md#masa-blazor-接入) | `MASA.Lingua.SDK.MasaBlazor`（`AddLinguaForMasaBlazor`） | 两种模式：独立应用启动拉一次、刷新页面生效；与 `AddLinguaMasaI18n` 同宿主时自动改用共享 store，跟随 SSE / 对账热更新 |
| .NET MAUI / Blazor Hybrid / WPF 等客户端应用 | [maui.md](maui.md) | `MASA.Lingua.SDK.Maui` | 启动与回到前台时同步，界面自动刷新 |
| 构建期（拉取兜底包、导入存量、CI 检查） | [cli.md](cli.md) | `lingua-sync`（`pull` / `push` / `check`） | — |

> 除 MAUI 与 Masa.Blazor 外，文档里的写法都在真实的示例工程里编译、运行并对着一个 Lingua 服务端验证过。
> MAUI 的核心逻辑有自动化测试（`tests/MASA.Lingua.Tests/MauiClientTests.cs`，27 条），但 MAUI 专有部分
> （XAML 绑定、`App.xaml.cs`、资源打包）没有在真机 / 模拟器上跑过；
> Masa.Blazor 包有两处**真实消费方**（MASA.Auth 的 SSO 门户：`AddLinguaForMasaBlazor` + `AddLinguaMasaI18n` 同宿主），
> 但仓内**没有自动化测试**（`tests/` 里没有引用该包），改动后请按文末「验证过什么」自行冒烟。
> 各文档末尾的「验证过什么」写明了验证了什么、没验证什么。

> **SDK 安装（nuget.org）**：版本号只在 `Directory.Build.props` 维护一处（当前 `0.0.1-preview.1`）。
> 在 GitHub 上打 `x.y.z` tag 并发布 Release 后，`package_push_nuget.org.yml` 工作流经 `build/pack.sh`
> 打包并推送到 [nuget.org](https://www.nuget.org/profiles/MASA)。消费方一律用 `PackageReference`。
> 需要在自己工程里验证未发版的改动时，见 [aspnetcore.md 的「临时验证未发版的改动」](aspnetcore.md#临时验证未发版的改动例外情况)。

## 内置示例项目

空库首次启动会自动播种这三个项目，可直接用来试接入：

| 项目编码 | 内容 | 语种 |
|---|---|---|
| `auth-web-staging` | MASA.Auth 的 Auth 管理端错误码（**项目根命名空间**，101 条平铺 key，**真实业务数据**） | zh-CN / en-US / ja-JP / ru-RU |
| `auth-sso-staging` | MASA.Auth 的 SSO 门户服务端错误码（**项目根命名空间**，16 条平铺 key，**真实业务数据**） | zh-CN / en-US / ja-JP / ru-RU |
| `shop-vue` | 虚构的 Vue 商城演示（`order` 命名空间），用来演示占位符、长度约束与「待复核」状态 | zh-CN / en-US / ja-JP |

> 种子是两个项目 `Assets/I18n/*.json` 的**快照**，不跟随 MASA.Auth 演进：`auth-web-staging` 的 101 条里有 2 条
> （`GuestAccountOperate` / `DemoEnvironmentForbidden`）在 MASA.Auth 当前版本已不在管理端资源文件中，而
> `auth-sso-staging` 的 16 条与 `Assets/I18n` 完全一致。SSO 登录页 UI 的 86 条文案在
> `wwwroot/i18n`，**不在**这两个种子里。
>
> 项目编码 = PM 中该应用在对应环境的 Identity。staging 环境下 MASA.Auth 的取值是
> `auth-service-staging`（Admin 服务）、`auth-web-staging`（Admin 网页）、`auth-sso-staging`（SSO）——
> 三个是不同的应用；种子用的是后两个。业务宿主不要照抄种子编码，应填自己应用在 PM 里的 Identity。

两个 `auth-*` 项目的错误码落在**项目根命名空间**，因为 MASA I18n 的调用形态是平铺 key
（`I18n.T("UserNotExist")` / `UserFriendlyException(errorCode: "UserNotExist")`），只有根命名空间才会发布成
`{"UserNotExist":"…"}` 这种扁平根 key。放进 `errors` 命名空间会发布成 `{"errors":{"UserNotExist":…}}`，
平铺 key 永远命不中——平台上改词也就不会对业务端生效。

两个项目同时继承全局共享命名空间 `common` / `errors`，可以用来看「共享层继承」的合并效果（发布产物里它们是子对象，
与根级裸 key 互不干扰）；平台还内置了 3 条演示术语（Lingua / 工作台 / 结算）用于术语强制。

> 种子只对**空库**生效。之前版本播过种、且把词条放在 `errors` 命名空间下的库，需要删掉这两个项目后重建
> （`DELETE /api/v1/projects/{项目}` 删除、`POST …/{项目}` 重建；或走「导入 JSON」并把 `defaultNs` 留空、手工清理旧词条），不会自动迁移。

## 服务端版本要求（部署方）

| 项 | 要求 | 说明 |
|---|---|---|
| 运行时 | **.NET 10** | `MASA.Lingua.Server` 及服务端依赖闭包（Storage / AI / Migrations）单目标 net10 |
| 客户端 SDK | **.NET 8 或 .NET 10** | `MASA.Lingua.Core` + 5 个 `SDK.*` 包（Core / Abstractions / AspNetCore / MasaI18n / MasaBlazor / Maui，共 6 个）均为 `net8.0;net10.0` 双目标，引用方按自身 TFM 自动选资产 |
| 数据库 | **PostgreSQL（唯一支持）** | 已移除 SQLite；连接串只认 `Lingua:Database:ConnectionString`（或 `ConnectionStrings:LinguaDb`）。凭据不入仓库：仓库内各 appsettings 的数据库连接串 / DCC Redis 密码一律留空，部署时通过环境变量 `Lingua__Database__ConnectionString`（Redis 用 `DCC__RedisOptions__Password`）注入，本地开发用 `dotnet user-secrets` 提供 |
| 副本数 | **单实例** | 发布缓存、manifest 统计与 SSE 广播都是**进程内状态**，站点不做跨实例校验：多副本部署会静默出错——A 实例发布后，B 实例继续吐旧 bundle，且 B 上的 SSE 客户端永远收不到发布事件。如需横向扩展，必须先把发布缓存与事件广播外置到共享存储（如 Redis），再放开副本数 |
| 发布产物目录 | `Lingua:BundleDirectory`（默认 `{ContentRoot}/App_Data/i18n`） | 只用于重启恢复与磁盘一致性校准，**不对公网暴露**（`/i18n/*` 永远走分发端点）。官方镜像以非 root（UID 1000，appuser，对齐 specdoc 的挂卷属主约定）运行，并已内置 `Lingua__BundleDirectory=/data/i18n`（权限 1777，任意运行 UID 可写，覆盖 `runAsUser` 的场景）；若把它挂成卷或覆盖此变量，请保证目录对实际运行用户可写（K8s 可用 `fsGroup`）；`readOnlyRootFilesystem` 下需把该路径挂成 `emptyDir`/卷。该目录可丢弃，容器重建后会从数据库发布快照补回 |
| 崩溃取证目录 | `Lingua:LogsDirectory`（镜像内置 `/data/logs`）；dump 目录镜像内置 `/data/dumps` | 文件日志与崩溃记录（crash.log）落日志目录；未处理异常的 minidump 落 dump 目录（`DOTNET_DbgMiniDump*`）。都随 `/data` 挂卷跨重启留存 |
| 管理后台认证 | **SSO 强制登录** | `Auth:ClientId` + DCC（`DCC` 段，MasaStackConfig 自动解析 SSO 域名），任一缺失即启动失败；仅测试/离线调试可用 `Auth:Authority` 覆盖 |
| DataProtection 密钥目录 | `DataProtection:KeysDirectory`（镜像内置 `/data/dataprotection`） | 认证 cookie 与 OIDC 状态由此加密；部署须把该路径挂载为卷，不挂则容器重建后全员登出（`docker restart` 不受影响）；目录不可写时启动期 fail fast；卷内旧运行 UID 遗留的不可读密钥文件会被自动跳过并告警（`chown 1000:1000` 或删除即可），不影响登录 |
| HTTPS（必须） | 反代/ingress 终止 TLS + 环境变量 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` | 管理后台 OIDC 登录的 correlation/nonce cookie 带 `Secure` 标记，纯 HTTP 下浏览器拒收、**登录无法完成**；开关让应用采纳 ingress 的 `X-Forwarded-Proto`（否则 `redirect_uri` 被拼成 `http://…`，SSO 把浏览器引回 http 后 cookie 不随行）。80→443 跳转由 ingress 在边缘完成（ingress-nginx 默认 `ssl-redirect`）；容器内发现不了 https 端口时应用层跳转不生效，需要兜底就设 `ASPNETCORE_HTTPS_PORT=443` |

## 先搞清楚三件事

### 1. 项目必须先存在于 Lingua

管理后台没有「项目与接入」页面：项目清单以 **MASA Stack PM** 为准（项目编码 = 应用在 PM 里的 Identity），项目、语种、默认语种都是平台里的配置：

- 项目配置的初始化有两条路：PM 里已有、Lingua 里还没有配置的应用，在管理后台选中后点「创建多语言配置」；
  或者全程脚本化 `POST /api/v1/projects/{项目}`（幂等创建骨架，新建时返回生成的写密钥）→ 导入 → 发布。
- 业务端只需要知道**项目编码**（例如内置示例 `auth-web-staging`）和 **Lingua 服务地址**。
- 业务端代码里**没有**语种列表和默认语种的配置项。平台启用了哪些语种、默认语种是什么，由
  `GET /api/v1/projects/{项目}/manifest` 告诉客户端。
- 项目**发布过**之后才有 manifest 和 `{culture}.json`。没发布时 manifest 返回 404，.NET 客户端会安静地等待，
  Vue 客户端 `syncLocale` 返回 `false`。

### 2. 下发的是什么

发布后平台生成固定文件名的静态 JSON：

```
GET {服务地址}/i18n/{项目}/{culture}.json              # 带强 ETag，未变化返回 304（Cache-Control: no-cache）
GET {服务地址}/api/v1/bundles/{项目}/{culture}          # 同上内容的兼容路由，支持 ?format=flat
GET {服务地址}/api/v1/projects/{项目}/manifest          # 版本指纹 + 已发布语种 + 默认语种
GET {服务地址}/api/v1/projects/{项目}/glossary          # 生效术语库（全局 + 项目，同名项目覆盖；带 ETag）
GET {服务地址}/api/v1/projects/{项目}/export-zip        # 导出**当前编辑状态**（含未发布草稿）的全部语种 zip，支持 ?format=flat
GET {服务地址}/api/v1/stream?project={项目}             # SSE：发布 / 回滚时推送，15s 心跳，retry: 3000
POST {服务地址}/api/v1/projects/{项目}                  # 幂等创建项目骨架；新建返回写密钥，已存在则 200 不改配置
DELETE {服务地址}/api/v1/projects/{项目}                # 删除项目（词条/术语/发布历史级联清除；服务端的发布缓存与磁盘产物一并清理，同名重建后需重新发布才有 manifest）
POST {服务地址}/api/v1/telemetry/missing-keys           # 缺失词条上报，请求体 { projectCode, culture, items }；需写密钥（仅服务端 SDK）
POST {服务地址}/api/v1/projects/{项目}/import-json?culture=&defaultNs=&publish=   # 导入草稿，publish=true 时立即发布；请求体上限 10MB，超限 413
```

> `export-zip` 与 `manifest` / `{culture}.json` 的数据来源不同：前者取数据库**当下的编辑状态**（管理员显式导出，
> 用于备份 / 迁移 / 交回业务仓库），后两者只出**已发布**内容。需要「和线上完全一致」的导出时请用
> `lingua-sync pull`（按 manifest 的 ETag 拉发布产物），不要用 export-zip。
>
> `import-json` 的 `defaultNs` **不传或留空 = 项目根命名空间**（顶层扁平 key 发布成 JSON 根级裸 key）。
> 只有 `lingua-sync push` 这个 CLI 包了一层「默认 `common`」的便捷值，直接调 HTTP 端点时没有那个默认值。

缓存约定：所有分发响应都是**强 ETag + `no-cache`**（发布产物公开，允许共享缓存存副本）。客户端每次都要带
`If-None-Match` 重新校验，未变时是 304；这样「发布完成」与「客户端看到新文案」之间没有额外延迟。

SSE 事件带 `id:`（单调递增的发布时刻）。浏览器 `EventSource` 断线重连会自动带 `Last-Event-ID`，
服务端据此补发漏掉的那一条（只补各项目最近一次发布）。**.NET 客户端 SDK 不实现补发**：它自己管理重连、
只解析 `data:` 行，重连成功后立即做一次全量对账来追赶（`LinguaJsonSyncService.ServerEventsLoopAsync`）。
事件载荷含 `projectCode` / `cultures[{culture, etag}]` / `defaultCulture` / `versionHash` / `publishedAt`——
带 ETag 是为了让客户端跳过 manifest 直接拉变化的语种。

**SSE 只承载「变了」这个通知，不承载文案内容**：内容永远由客户端按 ETag 主动拉取。因此事件丢失、
重复、乱序都只影响延迟，不影响正确性；SDK 另有空闲对账兜底，最迟一个周期追上线上版本。

`format=flat` 与嵌套格式是同一内容的两种表示，**共用同一个 ETag**（由同时覆盖两种字节的内容指纹派生：任一表示变化，ETag 都会变化）；
HTTP 缓存按完整 URL（含 `?format=flat`）分条目，混用不会串。**.NET 客户端 SDK（MAUI / ASP.NET / Blazor）一律下载扁平格式**——
手动下载内置语言包 / 自己写同步脚本给 .NET 用时，也请带上 `?format=flat`。

词条在 JSON 里按 `.` 分层。同一个词条在各端的写法：

| 平台里的 key | Vue | ASP.NET / Blazor |
|---|---|---|
| `errors.UserNotExist` | `t('errors.UserNotExist')` | `L["errors.UserNotExist"]` |
| `common.welcomeUser` | `t('common.welcomeUser', { userName })` | `L["common:welcomeUser", new { userName }]`（`:` 等价于 `.`） |

占位符是命名的 `{name}`：Vue 传对象，.NET 传匿名对象 / 字典，或用位置参数 `{0}`。
**忘了传参数时，界面上会原样显示 `{count}`。**

译文没有「已批准」的语种词条，发布时会回退成源语种文案，客户端看到的是源语种文字而不是 key。

**Key 与命名空间同名 / 键路径冲突**：

- 命名空间 `Confirm` + 其下词条 `Confirm`（取词写 `Confirm.Confirm`）**不冲突**，是两个层级的名字。
- 项目根的裸 key `Confirm` 与命名空间 `Confirm`（含 `Confirm.Title` 等词条）**会冲突**：嵌套 JSON 里 `"Confirm"` 这个位置
  不能既是字符串又是对象，嵌套文件只保留命名空间一侧；扁平文件两侧都保留（`"Confirm"` 与 `"Confirm.Title"` 并存）。
  同理，词条 `A/B` 与根 key `A.B`、根 key `A` 与根 key `A.B` 也会撞同一个位置。
- 平台对这类冲突的处理：发布门禁默认**阻断**（`KEY_PATH_CONFLICT`，可强制发布并留痕，或在发布策略里关闭阻断）；
  导入、手工新增会当场提示 / 拦截；客户端缺词上报会**忽略**会产生冲突的 key（不会替你建出必然冲突的词条）。
  只按扁平格式取词的项目（所有 .NET SDK 都是）可以在发布策略里放行，但 Vue 等嵌套消费方仍会丢掉被挤掉的一侧——**建议直接改名消除冲突**。
- **大小写：命名空间编码与 Key 一律忽略大小写**，`Confirm` 与 `confirm` 是同一个命名空间 / Key，不可能同时存在：
  - 存储层按 `lower(...)` 唯一；用不同大小写写入已存在的词条（后台新增、导入、缺词上报）都是**更新**，库里与发布产物保留**首次创建时的写法**。
    导入文件里仅大小写不同的重复项会被合并，并在导入结果里提示（`caseDuplicateCount` / `caseDuplicates`）。
  - 服务端与所有 .NET SDK（ASP.NET / Blazor / MAUI / MasaI18n）取词都忽略大小写：后台录的是 `Confirm.Title`，代码里写 `confirm.title`
    也能取到；`IStringLocalizer<OrderController>` 这类按类型解析的写法（前缀 `order.`）能找到命名空间 `Order`。
  - 例外：**Vue（vue-i18n）等 JS 运行时按大小写敏感取词**，要使用发布产物里的写法。
  - 升级须知：迁移会把唯一索引换成忽略大小写版本，库里若已有仅大小写不同的命名空间 / Key，迁移会中止并列出它们
    （不改任何数据），处理掉重复项后重新启动即可。

### 3. 访问策略

分发**读端点匿名**（站点部署在内网，读发布产物不需要凭据，SDK、CLI 与浏览器均可直接调用）；
**缺词上报是唯一带鉴权的端点**：必须携带项目写密钥（`X-Lingua-Key` 请求头，管理后台「接入密钥」面板获取），
且**只有服务端 SDK 支持上报**——MAUI / Vue 等客户端 SDK 已移除上报能力，密钥无法安全保管在不可信客户端。

- 导入端点的门禁是**项目必须存在**：项目编码写错时导入返回 404，不会凭空创建项目；创建项目要走显式的
  `POST /api/v1/projects/{项目}`（幂等）。
- **其余写类端点默认匿名**：`POST /api/v1/projects/{项目}` 能创建项目、`DELETE /api/v1/projects/{项目}` 能删除项目、
  `POST …/import-json?publish=true` 能改线上文案，`GET …/export-zip` 能拉走全量草稿。站点必须只在受控网段内暴露；
  一旦端口可达公网，任何能连上的人都能改文案、删项目。作为纵深防御，可配置共享密钥
  `Lingua:Distribution:WriteApiKey`（环境变量 `Lingua__Distribution__WriteApiKey`）：配置后上述四个端点必须携带
  `X-Lingua-Key` 请求头且与密钥匹配（缺失 401、不匹配 403），脚本化接入在请求头里带上密钥即可；留空（或全是空白）维持匿名（默认，内网部署）。
  - 这把共享密钥与项目的**写密钥**（缺词上报用，每个项目一把）是两回事，互不通用；它只守上面四个写类端点，读端点与缺词上报不受影响。
  - 密钥必须是高熵随机串（建议 ≥ 32 字符，如 `openssl rand -base64 32`），只通过环境变量 / 密钥管理注入，**不要写进仓库里的 appsettings**
    （仓库里 `Lingua:Distribution:WriteApiKey` 保持留空）。校验是常量时间比较，但抵挡不了弱密钥被猜。
  - **`lingua-sync push` 要带密钥**：`--key <密钥>` 或环境变量 `LINGUA_WRITE_KEY`（CI 请用 secret + 环境变量，避免密钥出现在进程列表里；
    CLI 不从 `lingua.config.json` 读密钥）。见 [cli.md](cli.md)。
  - **管理后台「下载全语种 ZIP」不受影响**：它走独立的 SSO 路由 `GET /admin/projects/{项目}/export-zip`（未登录 302 到 `/login`），
    浏览器导航带不了请求头，所以不用共享密钥；脚本 / CLI 想导出仍走上面带密钥的 `GET /api/v1/projects/{项目}/export-zip`。
- 项目编码（PM Identity）在创建端点与仓储层统一校验：只允许字母 / 数字 / 连字符（-）/ 下划线（_），长度 1–64——
  编码会用作发布产物目录名（`App_Data/i18n/{code}`），含路径分隔符或「..」的编码会被 400 拒绝，防止路径穿越。
- 管理后台（`/`、`/glossary`、`/import-export` 三个页面）走 **SSO 强制登录**，与上面的机器调用是两套鉴权，互不影响。
- 浏览器跨域**全放开**（`Access-Control-Allow-Origin: *`），任意来源的 Vue / Blazor Wasm 都能直接读；
  暴露 `ETag` 供客户端做条件请求。
- 需要区分调用方时，看 `X-Lingua-Client` 请求头里的客户端类型与「采用情况」统计，而不是密钥。

## 通用排错

| 现象 | 原因 |
|---|---|
| manifest / `{culture}.json` 返回 404 | 项目编码写错，或项目还没发布 |
| 导入返回 404 | 项目编码写错（平台不会自动创建项目） |
| 创建 / 删除 / 导入 / 导出返回 401 / 403 | 服务端配置了 `Lingua:Distribution:WriteApiKey`：401 = 请求没带 `X-Lingua-Key`；403 = 密钥与服务端配置不一致。`lingua-sync push` 用 `--key` / `LINGUA_WRITE_KEY` 提供（注意这不是项目的写密钥） |
| 创建项目返回 400「项目编码不合法」 | 编码含字母 / 数字 / `-` / `_` 以外的字符，或超过 64 位（编码会用作发布产物目录名，不能含路径分隔符、`..`、空格、中文） |
| 页面显示 key 本身（如 `errors.UserNotExist`） | 当前语种和默认语种里都没有这个 key；或还没发布 |
| 页面显示 `{count}` | 取词时没传占位符参数 |
| 缺失词条上报没反应 | 服务端返回 404：项目编码不存在；401：请求未带 `X-Lingua-Key`；403：密钥不匹配（与项目的写密钥不一致，重置过要同步更新配置）。成功时返回 `addedCount`。另外：`EnableMissingKeyTelemetry = false`、`ServerEndpoint` 为空、或 `WriteKey` 未配置时，客户端**根本不发请求**（后者只记一条进程级 Warning） |
| 平台改词后业务端没变 | 词条没发布（导入只写草稿）；或发布被门禁拦下（`publishBlockedReason`）；或该语种这条译文在「待复核」，发布时回退成源语种文案 |
