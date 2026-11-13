# 更新日志

MASA.Lingua 的所有显著变更记录于此。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循 [SemVer](https://semver.org/)。

[English](CHANGELOG.md)

## [Unreleased]

### 新增

- 可选的共享写密钥 `Lingua:Distribution:WriteApiKey`（环境变量 `Lingua__Distribution__WriteApiKey`）。配置后，写类端点——`POST` / `DELETE /api/v1/projects/{project}`、`import-json`、`export-zip`——必须携带匹配的 `X-Lingua-Key` 请求头（缺失 `401`、不匹配 `403`，常量时间比较）。留空或全是空白（默认）保持原来的匿名行为。读端点与缺词上报不受影响；它与「每个项目一把」的项目写密钥是两套密钥。请使用足够长的随机值并通过环境变量注入，不要写进会提交的 `appsettings`。
- `lingua-sync push --key <密钥>` / `LINGUA_WRITE_KEY` 携带共享写密钥。CLI 刻意不从 `lingua.config.json` 读取；遇到 `401` / `403` 时报错会提示如何提供或修正。
- `GET /admin/projects/{project}/export-zip`：管理后台的「下载全语种 ZIP」按钮改指向这里。它受管理后台的 SSO 策略保护（匿名请求 302 到 `/login`）——浏览器导航发不了 `X-Lingua-Key`，否则一配置 `WriteApiKey` 管理员反而下载不了。脚本仍可用 `/api/v1/.../export-zip`。
- `StaticBundlePublisher.DeleteProjectAsync`：一次调用完成「删项目 + 清理发布状态」，删除端点改用它，两步不会再各自漂移。
- `LinguaCultureFiles.IsSafeCultureName`（`MASA.Lingua.SDK.Abstractions`）：语种名作为文件名的统一安全规则，MAUI 与 ASP.NET Core SDK 共用。
- `LinguaLocalizer`（`MASA.Lingua.SDK.Maui`）可选接收 `ILogger<LinguaLocalizer>`（`AddLinguaMaui` 自动从容器取），每次同步失败记一条 Warning。
- 键路径冲突现在在**产生的当下**就会被发现，而不只是发布时才拦：根 key 与同名命名空间撞车（`Confirm` 对 `Confirm.*`）时，缺词上报会跳过（写进去就会破坏嵌套产物），管理后台「新增词条」对话框会提示，`import-json` 会在响应里用 `pathConflictCount` / `pathConflicts` 报告（只列本次导入新引入的冲突）。新增 `IEntryCatalog.FindKeyPathConflictsAsync`（默认实现返回空）、`KeyPathIndex`、`JsonImportResult.PathConflicts` / `PathConflictCount`。
- 发布检查新增不阻断的 `Warnings`，在管理后台展示：被发布策略放行的键路径冲突。
- `LinguaKeys`（`MASA.Lingua.Core`）：命名空间编码与词条 Key 的唯一比较规则（`OrdinalIgnoreCase`；比较器、供数据库查询用的 `Fold`）。`import-json` 会把上传文件内仅大小写不同的重复词条用 `caseDuplicateCount` / `caseDuplicates`（`JsonImportResult.CaseDuplicates` / `CaseDuplicateCount`）报告，`lingua-sync push` 会打印出来。
- 快照内容格式升到 v2，同时保存每个语种的嵌套**与**扁平产物（`SnapshotContentCodec.Pack` / `UnpackBundles`、`BundleCompiler.FromPublishedBytes(project, culture, nested, flat)`）；发布目录在 `{culture}.json` 旁多一个 `{culture}.flat.json`。旧版本写下的快照与目录仍可读取，见「修复」。

### 变更

- **命名空间编码与词条 Key 现在全链路忽略大小写。** `Confirm` 与 `confirm` 是同一个命名空间 / Key。此前它们是两个不同的东西，各客户端的处理还不一致：MasaBlazor 的 I18n 缓存会把它们合并，.NET SDK 却把它们当成两个。现在由同一条规则（`LinguaKeys`，`OrdinalIgnoreCase`）统一管住：存储唯一性、新增 / 删除 / 导入 / 缺词上报的查找、共享命名空间继承、发布合并（项目层按忽略大小写的身份覆盖共享层）、冲突检测，以及所有 SDK 的取词（`LinguaFrozenMemoryStore`、`IStringLocalizer`、MAUI `LinguaLocalizer`、MasaI18n 桥）。用不同大小写写入已存在的词条是「更新」；库里保留词条首次创建时的写法，发布产物也用它。
- **升级须知：** 迁移 `CaseInsensitiveKeyUniqueness` 会把三条唯一索引换成 `lower(...)` 表达式索引。若库里已经有仅大小写不同的命名空间或 Key，迁移会中止并报错列出它们（每类最多 20 组），不会修改任何数据——请合并或删除重复项后再启动站点。此前靠大小写区分而合法的 Key 不再允许。局限：项目编码不变；vue-i18n 等 JS 运行时按大小写敏感取词，Vue 应用需使用发布产物里的写法；`lower()` 对非 ASCII 字母的处理取决于数据库区域设置。
- 站点每次启动都会执行 EF Core 迁移（此前就有，写法改为参照 MASA.Auth 的 `MigrateDbContextAsync`）：新增 `MigrateLinguaDatabaseAsync`——先查询待执行的迁移，没有就只记一条「已是最新」，有就把待执行的迁移名写进日志再执行；失败时记 Critical 并原样抛出，站点不会在旧表结构上继续启动。迁移在后台服务与端点就绪之前完成；EF Core / Npgsql 的数据库级咨询锁让多副本同时启动也是安全的。`InitializeLinguaDatabaseAsync`（迁移 + 空库种子数据）行为不变。
- 嵌套 JSON、扁平 JSON 与冲突检测统一使用同一条路径规则（`JsonBundleConverter.ToPathSegments`），门禁报告的冲突与产物实际行为完全一致。命名空间 / Key 的各段会 Trim、丢弃空段；路径最终为空的词条被跳过，不再产出 `""` 键。`import-json` 对读入的 Key 做同样的规范化。
- 草稿与线上版本的差异比较、manifest 的 `entryCount` 改为基于扁平产物（.NET SDK 实际下载的那一份）：只存在于键路径冲突扁平侧的改动也能在差异里看到；`entryCount` 统计的是已发布词条数，而不是查找别名数。
- `MASA.Lingua.SDK.AspNetCore`：manifest 没有 `flatUrl` 时，同步服务默认下载扁平产物（`LinguaProtocol.FlatBundlePath`）；缺词上报会 Trim。

- `MASA.Lingua.SDK.AspNetCore`：同步服务补上本地 `{culture}.json` 的对账——语种文件被删后不再带 `If-None-Match`，304 不会让磁盘永远补不回来（MAUI 客户端早已如此）；已被平台移除的语种不再算「缺文件」。manifest / SSE 事件里的语种名不是安全文件名时会被跳过并记 Warning，`SyncCultureFileAsync` 对不安全的语种名抛 `ArgumentException`，写盘重试的等待也不再占着线程池线程。
- `MASA.Lingua.SDK.Maui`：`LastSyncError` 改为在同步闸门内记录，并发同步后它始终对应最后完成的那一次（迟到的失败不会再覆盖后面成功的结果）；失败同时写入日志器。
- `.github/dependabot.yml` 只对 `Microsoft.Extensions.Http`、`Microsoft.Extensions.Localization.Abstractions`、`Microsoft.Extensions.Hosting.Abstractions` 忽略主版本升级，不再屏蔽整个 `Microsoft.Extensions.*`，`Microsoft.Extensions.AI*` 照常收到升级 PR。
- `MASA.Lingua.SDK.Maui`：`TrySyncAsync` / `InitializeAsync` 在联网同步失败时一律不抛异常，仅调用方自己取消仍会抛出。此前只吞 `HttpRequestException` 与超时，登录页 HTML、磁盘写满、地址写错等失败会一路抛进应用启动路径。失败原因记录在新增的 `LinguaLocalizer.LastSyncError`（下一次同步成功后清空）；`SyncAsync` 仍照常抛出，同时记录。
- `MASA.Lingua.SDK.Maui`：语种包改为**先校验内容、再原子写缓存目录、最后才更新内存**，两个失败方向都不会让应用与磁盘分叉：「200 但内容不是合法 JSON」的响应不会再覆盖好缓存；内容合法但写盘失败（磁盘满、权限、被占用）时，内存里的词条与 ETag 也不会被推进，不会出现「本次会话是新文案、重启后又回到旧缓存」。两种情况旧缓存、内存词条与 ETag 都保持原样，下次同步重试。`Seed` 采用同样的顺序。
- `MASA.Lingua.SDK.Maui`：`AddLinguaMaui` 在注册期校验配置——缺 `ProjectCode`、或非空的 `ServerEndpoint` 不是 `http(s)` 绝对地址时抛 `InvalidOperationException`（留空仍是合法的纯离线 / 仅 Seed 模式）。命名 `HttpClient`（`LinguaClient`）新增 30 秒单次请求超时（`LinguaMauiExtensions.DefaultRequestTimeout`），应用可用自己的 `ConfigureHttpClient` 覆盖。
- `MASA.Lingua.SDK.Maui`：`Seed` 先规范化语种名（`zh-cn` → `zh-CN`）再决定缓存文件名，避免 Android（文件系统区分大小写）与 iOS（默认不区分）的缓存内容分叉。
- `MASA.Lingua.SDK.Maui` 与 `MASA.Lingua.SDK.MasaBlazor` 的 `net8.0` 目标改为依赖 `Microsoft.Extensions.*` 8.0.x（`Http` 8.0.1、`Localization.Abstractions` 8.0.34、`Hosting.Abstractions` 8.0.1），不再是 10.0.14，.NET 8 应用不会被迫升级整套 `Microsoft.Extensions.*`；`net10.0` 目标不变。Dependabot 已配置忽略这三个包的主版本升级。

### 修复

- **同名的根 Key 与命名空间（例如 Key `Confirm` + 命名空间 `Confirm`）在服务重启、回滚或磁盘恢复之后，客户端取不到它的译文。** 这几条恢复路径都是用嵌套 JSON 重建扁平产物，而嵌套里一个路径只能是值*或*对象，根 Key 因此从扁平产物里消失、ETag 也跟着变。现在两种产物都会持久化（快照 v2、`{culture}.flat.json`）并按字节原样还原，ETag 与之前一致。旧版本写下的快照会在启动时用草稿重编译做一次对账——但只有重编译结果的版本指纹与当初发布的完全一致才会采用，否则按原样还原旧内容并记 Warning。
- `MASA.Lingua.SDK.AspNetCore`：强类型 `IStringLocalizer<T>` 能为 `IStringLocalizer<Confirm>` 找到命名空间 `Confirm`（命名空间与 Key 的匹配忽略大小写）；缺词仍按小写的类型名前缀上报。
- `MASA.Lingua.SDK.MasaBlazor`：若服务端仍下发仅大小写不同的 Key（旧版本服务端），它们在不区分大小写的 I18n 缓存里会合并成一条；现在会记一条 Warning，而不是悄悄发生。
- 损坏的 `{culture}.flat.json` 不再让启动失败：忽略它（记 Warning），由嵌套文件重建扁平产物。
- `DELETE /api/v1/projects/{project}` 现在会一并清理发布器里该项目的状态：内存包缓存、manifest 统计、SSE 补发记录与 `App_Data/i18n/{code}` 目录。此前删除后缓存仍在，`/i18n/{code}/{culture}.json` 对已删项目照样返回 200；同名重建的项目在没发布之前就会读到上一个项目的文案。
- 磁盘文件只在数据库确认项目发布过（`LastPublishedAt`）时才被采信。从未发布过的项目不再对外提供、也不再在启动时恢复孤儿 `{culture}.json`，因此会正常执行引导发布，草稿差异也不再拿陈旧内容当对比基线。发布时还会删除不属于线上版本的 `{culture}.json` 残留文件。
- `POST /api/v1/projects/{project}`：两个调用方并发创建同一个新项目时，只有真正插入这一行的一方返回 `created: true` 与写密钥，另一方返回 `created: false` 且不带密钥。新增 `IProjectCatalog.CreateProjectIfAbsentAsync`，告诉调用方这一行是不是本次调用创建的。
- 编码早于校验规则的遗留项目，请求语言包 / manifest 时返回 404 而不是 500；修改这类项目的语种也不再被发布目录校验卡住。
- 缺词上报把上报语种写进词条的上下文说明（草稿译文仍只建在项目默认语种上：上报的 defaultText 是源语言文案，不是译文）。
- `If-None-Match` 处理改为只去掉恰好一个 `W/` 前缀，不再用 `TrimStart` 吃掉标签开头任意个 `W` 或 `/`。
- 并发的冷路径请求不会再在项目被删除期间把包缓存重新填回去（清理纪元会让清理开始之前启动的恢复作废）。原子写失败（发布器与 MAUI 缓存）不再残留 `.tmp` 文件。
- 管理后台：为校验不通过的项目编码（或其它任何失败）创建本地配置时，改为 toast 提示（意外异常另记日志），不再以未处理异常的形式冒出来。

### 安全

- 凡是创建项目的地方都校验项目编码——HTTP 端点、仓储层、发布器（`LinguaProjectCodes`）：只允许 ASCII 字母、数字、`-`、`_`，长度 1–64，不做 Trim。编码会成为发布产物目录名（`App_Data/i18n/{code}`），此前匿名调用方可以创建出「发布产物写到 `i18n` 目录之外」的项目（路径穿越）。发布器对校验上线前已入库的项目保留防御性检查。
- MAUI 与 ASP.NET Core SDK 里会变成本地文件名的语种名，限定为字母、数字、`-`、`_`（最长 35 字符），畸形或被篡改的 manifest 无法把文件写到本地缓存目录之外。
- 随仓库发布的 `appsettings*.json` 里的数据库连接串与 DCC Redis 密码已移除（改由环境变量或 user-secrets 提供，见接入指南）。**更早的提交里出现过的凭据仍留在 git 历史中——请轮换。**

### 文档

- 接入指南 README、MAUI 指南与中英文架构文档补充 Key / 命名空间重复规则：什么算冲突（`Confirm` Key + `Confirm.*` 命名空间；命名空间 `Confirm` 下的 Key `Confirm` 不算），如何检测与拦截，为什么 .NET SDK 读的是扁平产物，命名空间与 Key 忽略大小写的规则，以及快照 v2 / `{culture}.flat.json` 的目录布局。
- 接入指南、CLI 指南、架构文档与快速开始：补充共享写密钥（强度建议、`lingua-sync push --key` / `LINGUA_WRITE_KEY`、401 / 403 排错）、SSO 管理后台导出路由、项目编码规则，以及 ASP.NET Core 同步的对账行为。更正 MAUI 测试条数（README 与 `maui.md` 此前不一致，现统一为 27）。
- MAUI 接入指南：补充线程契约（变更通知在调用同步方法的上下文里触发，SDK 刻意不使用 `ConfigureAwait(false)`，有回归测试守着）、`LastSyncError`、注册期校验与超时、启动路径不要 `await InitializeAsync`、应用需自行持久化用户选择的语种，以及一条未经验证的 iOS `CFBundleLocalizations` 提示；「没有验证」清单同步扩充。

## [0.0.1-preview.2]

`0.0.1-preview.1` 的包推送之后落地的打包修复（nuget.org 上的 `0.0.1-preview.1` 包由最初的发布 tag 构建，不含本节变更）。

### 新增

- 发布门禁新增键路径冲突检测（`KEY_PATH_CONFLICT`）：项目根裸 key 与同名命名空间共存（或完整路径重复）时，嵌套发布 JSON 会丢失其中一侧文案。新增策略开关 `BlockOnKeyPathConflicts`（默认开启）；按扁平格式取词的项目可以关闭本检查。门禁摘要最多逐条列出 5 处冲突，超出部分汇总。
- 发布清单：每个语种新增可选 `flatUrl` 字段，指向扁平格式产物（`?format=flat`）。

### 修复

- 包 ETag / 版本指纹改为同时覆盖嵌套与扁平两种字节：键路径冲突词条的文案只存在于扁平文件中，此前修改译值时指纹（与客户端 ETag）纹丝不动，客户端永远收不到更新。
- .NET SDK 包（`AspNetCore` / `MasaBlazor` / `Maui`）下载语言包时优先使用扁平格式（清单 `flatUrl`；SSE 触发的同步用 `?format=flat` 拼接），服务端未提供时回退嵌套地址。

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
