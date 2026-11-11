using MASA.Lingua.AI;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Server.Components;
using MASA.Lingua.Server.Endpoints;
using MASA.Lingua.Server.Options;
using MASA.Lingua.Server.Services;
using MASA.Lingua.Server.Startup;
using MASA.Lingua.Storage.EfCore;
using MasaDesign.Blazor; // AddMasaDesign()：皮肤包注册扩展（AddMasaBlazor + UseMasaDesign）
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using Serilog.Events;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);

// 全局异常兜底最先注册：越早挂钩子，越早期的崩溃越有痕迹。
// 崩溃记录落盘目录与文件日志同目录（Lingua:LogsDirectory，容器里默认 /data/logs）；
// 本地开发默认 ContentRoot/App_Data/logs。目录写不进时只留 Console.Error 一份，不阻塞启动。
// 注意 SIGKILL（OOMKilled / 探针超时被杀）不触发这些钩子 —— 那类死亡由 GC 硬上限与内存采样日志兜底。
var logsDirectory = ResolveLogsDirectory(builder);
CrashReporter.Register(logsDirectory);

// 日志：console + 文件双写。
// 文件日志的意义：容器 stdout/stderr 驱动只在 pod 存续期间可读（重启后旧日志随容器一起消失），
// /data/logs 落在挂卷上，重启后上一轮崩溃前的日志还在 —— 这是「不接观测平台也能查事故」的基本盘。
// flushToDiskInterval=1s：日常异步批量写（不拖慢请求），崩溃前最多丢 1 秒日志。
// Error 级起走 stderr（无缓冲，被 SIGKILL 前已写透）；级别口径与原 Logging:LogLevel 一致。
builder.Host.UseSerilog((context, _, loggerConfiguration) => loggerConfiguration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Error)
    .WriteTo.File(
        Path.Combine(logsDirectory, "lingua-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        flushToDiskInterval: TimeSpan.FromSeconds(1)));

// 内存采样：30s 采一次，只在工作集相对上次落日志的值变化 ≥30MB 时记一条（首条为基线）；
// 稳态不刷屏，OOMKilled 后仍能靠它回放死亡前的内存曲线。
builder.Services.Configure<MemorySamplerOptions>(builder.Configuration.GetSection(MemorySamplerOptions.SectionName));
builder.Services.AddHostedService<MemorySamplerService>();

// 只支持 PostgreSQL，连接串来自 Lingua:Database:ConnectionString
builder.Services.AddSingleton<BundleCompiler>();
builder.Services.AddLinguaEfCoreStorage(builder.Configuration);

builder.Services.AddLinguaAi(builder.Configuration);

// 静态文件 {culture}.json 发布器与 SSE 广播器（发布通知的唯一实时通道）
builder.Services.AddSingleton<LinguaSseBroadcaster>();
builder.Services.AddSingleton<AdoptionTracker>();
builder.Services.AddSingleton<StaticBundlePublisher>();

// 管理后台顶栏的「当前项目」状态：每个 Blazor 电路一份，三个页面共用
builder.Services.AddScoped<WorkspaceScope>();

// 词条矩阵的 AI 翻译编排（按语种并发 + 批量写回）：无状态，单例即可
builder.Services.AddSingleton<EntryAiTranslator>();

// 项目切换器的数据源：MASA Stack PM 项目应用清单（60s 内存缓存，避免每个电路都打 PM）。
// 请求带超时上界：内网正常亚秒级，5s 已极宽松；部署网络到 PM/SSO「黑洞」（SYN 被丢弃无响应）时
// 靠它把「挂起」转成可呈现、可重试的失败，而不是布局初始化永远卡住。
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IPmProjectDirectory>(sp => new PmProjectDirectory(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IMemoryCache>(),
    sp.GetRequiredService<ILogger<PmProjectDirectory>>(),
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Lingua:Pm:RequestTimeoutSeconds", 5))));

// 右下角浮动提示：每个 Blazor 电路一份，由 MainLayout 里的 ToastHost 渲染
builder.Services.AddScoped<ToastService>();

// 管理后台 UI 组件库 + masa-design 皮肤（MASA Stack 品牌皮肤）：
// AddMasaDesign 内部即 AddMasaBlazor + UseMasaDesign，注册 masa-design-light / masa-design-dark 两套主题。
// 皮肤 CSS 的颜色 token 只作用于 html[data-skin="masa-design"]，属性由 App.razor 的首帧内联脚本设置。
builder.Services.AddMasaDesign();

// 跨域：内网部署，前端（Vue / Blazor Wasm）与服务端分属不同来源，读发布产物、上报缺失词条、导入都是浏览器直连，
// 因此统一全放开；没有 Cookie 凭据参与，不需要 AllowCredentials。
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .WithExposedHeaders("ETag"));
});

// 管理后台鉴权：SSO 强制登录（Cookie 会话 + OIDC challenge），无匿名模式。
// Authority 由 DCC（MasaStackConfig）解析，缺失即启动失败；本地调试/测试用 Auth:Authority 显式覆盖。
// 该步骤会改写配置数据源，必须排在所有 Auth:* 绑定之前。
var authority = await MasaStackStartup.InitializeAsync(builder);
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

// DataProtection 密钥持久化：认证 cookie 与 OIDC correlation/nonce 状态由密钥加密 ——
// 不持久化时容器重启后密钥全部重生成，所有管理后台会话失效、进行中的 OIDC 登录作废
// （曾接受「重启全员登出」的代价，现按 masa.specdoc 的部署模式恢复持久化）。
// 优先级：DataProtection:KeysDirectory（镜像内置 /data/dataprotection，部署挂卷）→
// 本地开发默认 %LOCALAPPDATA%\masa.lingua\dataprotection。
var keysDirectory = builder.Configuration["DataProtection:KeysDirectory"];
if (string.IsNullOrWhiteSpace(keysDirectory))
{
    keysDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "masa.lingua",
        "dataprotection");
}

// 运行用户缺 HOME 时 GetFolderPath 返回空串，Path.Combine 退化为相对路径 → 密钥目录落到 CWD。
// 此类部署错误必须启动期暴露，而不是等首次 OIDC challenge 才炸。
if (!Path.IsPathRooted(keysDirectory))
{
    throw new InvalidOperationException(
        "DataProtection 密钥目录解析为相对路径（运行用户缺少 HOME / XDG_DATA_HOME 且未配置 DataProtection:KeysDirectory）。"
        + "请部署侧设置 HOME，或通过环境变量 DataProtection__KeysDirectory 指定可写目录（如 /data/dataprotection）。");
}

// 路径绝对但不可写（挂载卷属主与运行 UID 不匹配）同样在启动期暴露，而不是登录时才懒触发。
try
{
    Directory.CreateDirectory(keysDirectory);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    throw new InvalidOperationException(
        $"DataProtection 密钥目录不可写：{keysDirectory}。"
        + "请检查挂载卷属主与运行用户（UID）是否匹配，或通过环境变量 DataProtection__KeysDirectory 指定可写目录。", ex);
}

// 密钥仓库：自研容错版（LinguaXmlKeyRepository）替换默认 FileSystemXmlRepository——后者枚举到读不了的
// key 文件（挂载卷里旧 UID 遗留的 0600 文件）会让整环加载失败、首个 OIDC 登录 500；容错版逐文件跳过并告警。
// 不调 PersistKeysToFileSystem（它会把仓库写死到 options.XmlRepository），改走 DI：XmlKeyManager 在
// options.XmlRepository 为空时回退解析 IXmlRepository。
builder.Services.AddDataProtection()
    .SetApplicationName("MASA.Lingua");
builder.Services.AddSingleton<IXmlRepository>(sp =>
    new LinguaXmlKeyRepository(keysDirectory, sp.GetRequiredService<ILoggerFactory>()));

builder.Services.AddLinguaAuth(authOptions, authority);

// 级联参数，缺失时 AuthorizeRouteView 直接抛异常（而不是降级为匿名渲染）。
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // 电路单条消息的接收上限：默认 32KB，「导入现有 JSON」文本框里粘贴的内容失焦时整段回传，稍大一点的 JSON 就会超限、直接断开电路。
    // 放宽到 1MB（文本框限制 25 万字符，UTF-8 最坏 3 字节也不到 1MB；更大的文件走文件选择，内容只在服务端，不经过这条消息）。
    // 不放得更宽：这是每个电路都能发送的单条消息上限，保持它与「管理员粘贴一段 JSON」的实际需要同量级。
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 1024 * 1024);

// 响应压缩：只对分发热路径有意义（语言包是重复度极高的 JSON 文本）。
// 默认 MimeTypes 已含 application/json，text/event-stream 不在集内 —— SSE 天然不压缩、不缓冲。
// EnableForHttps：生产全 HTTPS，不开启等于完全不压缩；响应体是已发布的静态语言包，
// 不含用户可控输入的反射，BREACH 类侧信道不适用。
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

var app = builder.Build();

// SSO 地址解析后只存在内存里（OIDC Authority 与 PM 应用身份 token 请求共用同一值），
// 不落日志则部署后无处可见；token 请求地址即 {该值}/connect/token。
app.Logger.LogInformation("SSO Authority:{Authority}", authority);

// HSTS 仅生产：告诉浏览器一年内该主机一律走 https（specdoc 同款）。
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// 压缩必须尽早进管线：晚于任何写响应头的中间件都无效。
// 仅生产启用：开发环境 Brotli 压缩会让 dotnet-watch / Browser Link 无法注入热刷新脚本
// （警告 "Unable to configure browser refresh script injection ... Content-Encoding: 'br'"），浏览器热刷新直接失效。
if (!app.Environment.IsDevelopment())
{
    app.UseResponseCompression();
}

// 应用数据库迁移并写入种子数据；随后从已发布的静态文件恢复内存缓存。
// 启动不再无条件重新发布：重启不会产生新版本、不会广播，更不会把未发布的草稿推上线（仅从未发布过的项目做一次引导发布）。
await app.Services.InitializeLinguaDatabaseAsync();
using (var scope = app.Services.CreateScope())
{
    var projectCatalog = scope.ServiceProvider.GetRequiredService<IProjectCatalog>();
    var publisher = scope.ServiceProvider.GetRequiredService<StaticBundlePublisher>();
    // 只需要项目编码：用概览投影（一条轻量查询），不整库加载词条树
    var projects = await projectCatalog.GetProjectOverviewsAsync();

    var failed = new List<string>();
    foreach (var proj in projects)
    {
        try
        {
            await publisher.EnsurePublishedAsync(proj.Code);
        }
        catch (Exception ex)
        {
            failed.Add(proj.Code);
            app.Logger.LogError(ex, "项目 {Project} 的发布产物恢复失败，其余项目继续启动。", proj.Code);
        }
    }

    if (failed.Count > 0)
    {
        app.Logger.LogWarning(
            "启动恢复完成，但有 {Count} 个项目失败：{Projects}。这些项目的分发端点会返回 404，修复后重新发布即可。",
            failed.Count,
            string.Join(", ", failed));
    }
}

// 跨域：全放开（见上方 AddCors 的说明）。必须早于端点映射，预检请求由中间件直接应答。
app.UseCors();

// 健康探针（匿名）。拆成两个语义，避免「数据库抖动 → liveness 失败 → 重启风暴」：
// - /health/live：纯进程存活，零依赖即时应答。liveness 用它 —— 只要进程还活着就不重启。
// - /health/ready：数据库可连。readiness 用它 —— 数据库抖动时摘流量，但进程保留，恢复后自动回归。
// - /health：兼容旧配置（= ready 语义）。部署侧 liveness 仍指向它的话，DB 慢查询/线程池打满时
//   kubelet 会按探针超时杀 pod —— 这正是「pod 无声重启、日志里什么都没有」的一类来源。
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/ready", async (IDbContextFactory<LinguaDbContext> dbFactory, CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "degraded" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapGet("/health", async (IDbContextFactory<LinguaDbContext> dbFactory, CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "degraded" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

// 登录入口（/login、/logout）必须在分发端点之前映射，避免被 /i18n 前缀分支影响
app.MapLinguaAuthEndpoints();

// 映射 /i18n/{project}/{culture}.json 协商缓存端点、导入导出与缺失 Key 上报 API
app.MapLinguaDistributionEndpoints();

// /i18n/* 只由分发端点提供（ETag 与 manifest 一致、记录采用情况、永远是已发布版本），不让静态文件中间件按磁盘文件直接响应。
// 发布产物已挪到 App_Data/i18n（不对公网暴露），这条排除依然保留：即使有人把产物拷进 wwwroot，也绕不过分发端点的鉴权与统计。
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/i18n"),
    branch => branch.UseStaticFiles());

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// 日志/崩溃记录目录：Lingua:LogsDirectory 显式配置优先；容器里由 Dockerfile 环境变量指向 /data/logs
//（与 /data/i18n 同一挂卷模式，跨重启留存）；本地开发落到 ContentRoot/App_Data/logs。
static string ResolveLogsDirectory(WebApplicationBuilder builder)
{
    var configured = builder.Configuration["Lingua:LogsDirectory"];
    return string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "logs")
        : configured;
}

public partial class Program;
