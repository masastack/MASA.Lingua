using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MASA.Lingua.Storage.EfCore;

public sealed class LinguaDatabaseOptions
{
    public const string SectionName = "Lingua:Database";

    /// <summary>
    /// PostgreSQL 连接字符串。主配置位置 Lingua:Database:ConnectionString；
    /// 未配置时回退 .NET 标准位置 ConnectionStrings:LinguaDb（便于 ConnectionStrings__LinguaDb 环境变量注入）。
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}

public static class LinguaStorageExtensions
{
    /// <summary>
    /// 通过 IConfiguration 绑定 PostgreSQL 连接字符串。
    /// </summary>
    public static IServiceCollection AddLinguaEfCoreStorage(
        this IServiceCollection services,
        IConfiguration configuration)
        => services.AddLinguaEfCoreStorage(ResolveConnectionString(configuration));

    /// <summary>
    /// 显式指定 PostgreSQL 连接字符串。空值视为配置缺失，直接抛出（不做任何默认库兜底）。
    /// </summary>
    public static IServiceCollection AddLinguaEfCoreStorage(
        this IServiceCollection services,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{LinguaDatabaseOptions.SectionName}:ConnectionString 未配置 —— 站点只支持 PostgreSQL，没有默认库兜底。"
                + "请配置 Lingua:Database:ConnectionString（或环境变量 Lingua__Database__ConnectionString）。");
        }

        services.AddDbContextFactory<LinguaDbContext>(options => ConfigureDbContextOptions(options, connectionString));
        services.AddSingleton<ILinguaRepository, EfCoreLinguaRepository>();

        // 主体接口与门面解析到同一个实例：管道类注入它真正操弄的主体（见各接口文档），
        // Blazor 管理页与测试继续用 ILinguaRepository 门面，两者不产生第二份状态
        services.AddSingleton<IProjectCatalog>(sp => sp.GetRequiredService<ILinguaRepository>());
        services.AddSingleton<IEntryCatalog>(sp => sp.GetRequiredService<ILinguaRepository>());
        services.AddSingleton<IGlossaryCatalog>(sp => sp.GetRequiredService<ILinguaRepository>());
        services.AddSingleton<IPublishArchive>(sp => sp.GetRequiredService<ILinguaRepository>());
        return services;
    }

    /// <summary>
    /// 解析生效的连接字符串：Lingua:Database:ConnectionString 优先，其次 ConnectionStrings:LinguaDb。
    /// </summary>
    public static string ResolveConnectionString(IConfiguration configuration)
    {
        var fromSection = configuration[$"{LinguaDatabaseOptions.SectionName}:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(fromSection))
        {
            return fromSection;
        }

        return configuration.GetConnectionString("LinguaDb") ?? string.Empty;
    }

    public static void ConfigureDbContextOptions(DbContextOptionsBuilder options, string connectionString)
    {
        options.UseNpgsql(connectionString, npgsql =>
        {
            // 不开 EnableRetryOnFailure：EF 的重试执行策略与「用户显式事务」互斥
            // （仓储里 UpsertEntry / RecordPublishSnapshot 都用 BeginTransaction 保证多次写入原子）。
            // 真要重试，正确做法是把整段工作包进 Database.CreateExecutionStrategy()，这里不做。
            npgsql.MigrationsAssembly(PostgreSqlMigrationsAssembly);
        });
    }

    /// <summary>PostgreSQL 迁移所在程序集（由宿主项目引用，不能被本程序集反向引用）。</summary>
    public const string PostgreSqlMigrationsAssembly = "MASA.Lingua.Storage.Migrations.PostgreSql";

    /// <summary>
    /// 应用 EF Core 迁移（站点<b>每次启动</b>都会调用，写法参照 MASA.Auth 的 <c>MigrateDbContextAsync</c>）：
    /// 先看有没有待执行的迁移，有才执行，并把执行了哪些迁移写进日志；已是最新就什么都不做。
    /// <para>
    /// 迁移失败（连不上库、迁移脚本报错，例如 <c>CaseInsensitiveKeyUniqueness</c> 发现库里有仅大小写不同的重复项）会记 Critical 日志并
    /// <b>原样抛出</b>：站点启动失败，而不是带着旧表结构继续跑。库不存在时由 <c>MigrateAsync</c> 负责创建。
    /// 多副本同时启动是安全的：EF Core 迁移自带数据库级咨询锁，同一时刻只有一个副本在执行，其余副本等锁并发现已是最新。
    /// </para>
    /// </summary>
    public static async Task MigrateLinguaDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("MASA.Lingua.Storage.Migration");

        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count == 0)
            {
                logger?.LogInformation("数据库迁移：已是最新，没有待执行的迁移。");
                return;
            }

            logger?.LogInformation(
                "数据库迁移：有 {Count} 个待执行的迁移（{Migrations}），开始执行。",
                pending.Count,
                string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);
            logger?.LogInformation("数据库迁移：执行完成。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogCritical(ex, "数据库迁移失败，站点不会继续启动（不会在旧表结构上运行）。请按错误信息处理后重新启动。");
            throw;
        }
    }

    /// <summary>
    /// 应用 EF Core 迁移（<see cref="MigrateLinguaDatabaseAsync"/>）并（空库时）填充内置数据：全局共享命名空间、演示术语库、
    /// 演示项目 shop-vue，以及两个真实业务示例项目（MASA.Auth 的 auth-web-staging / auth-sso-staging）。
    /// <para>
    /// <b>只对空库生效</b>：一旦库里有任意项目就直接返回。因此修改种子内容（例如把 auth-* 两个项目的词条
    /// 从 <c>errors</c> 命名空间改到项目根）不会自动迁移已有数据——需要删掉旧项目后重建，或走
    /// 后台「导入 JSON」并把 <c>defaultNs</c> 留空、手工清理旧 <c>errors</c> 命名空间下的重复词条（项目骨架
    /// 也可用 <c>POST /api/v1/projects/{project}</c> 幂等创建）。这里不做自动迁移：种子只是演示数据的初始形状，不是业务数据。
    /// </para>
    /// </summary>
    public static async Task InitializeLinguaDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await services.MigrateLinguaDatabaseAsync(ct);

        var factory = services.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        if (await db.Projects.AnyAsync(ct))
        {
            return;
        }

        // 1. 创建全局共享命名空间 common 与 errors（供所有 Vue / Blazor / ASP.NET 项目自动继承）
        var sharedCommon = new TranslationNamespace
        {
            Code = "common",
            IsShared = true,
            Entries =
            [
                CreateEntry("ok", "确定", "Confirm", "確認", "通用确认按钮", maxLength: 10),
                CreateEntry("cancel", "取消", "Cancel", "キャンセル", "通用取消按钮", maxLength: 12),
                CreateEntry("save", "保存修改", "Save Changes", "変更を保存", "表单底部保存按钮", maxLength: 16),
                CreateEntry("welcomeUser", "欢迎回来，{userName}！", "Welcome back, {userName}!", "お帰りなさい、{userName}さん！", "顶部导航栏欢迎语，含命名占位符")
            ]
        };

        var sharedErrors = new TranslationNamespace
        {
            Code = "errors",
            IsShared = true,
            Entries =
            [
                CreateEntry("networkTimeout", "网络连接超时，请稍后重试", "Network timeout, please try again later", "ネットワークがタイムアウトしました", "网络请求失败提示"),
                CreateEntry("unauthorized", "登录状态已过期，请重新登录", "Session expired, please sign in again", "セッションの有効期限が切れました", "401 鉴权提示")
            ]
        };

        db.TranslationNamespaces.AddRange(sharedCommon, sharedErrors);

        // 2. 创建全局术语库 (Glossary)
        db.GlossaryTerms.AddRange(
            new GlossaryTerm
            {
                SourceTerm = "Lingua",
                Description = "多语言平台品牌名称，任何语言均保持原样不可意译",
                DoNotTranslate = true,
                CaseSensitive = true
            },
            new GlossaryTerm
            {
                SourceTerm = "工作台",
                Description = "系统主控面板统一术语",
                DoNotTranslate = false,
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["en-US"] = "Workspace",
                    ["ja-JP"] = "ワークスペース"
                }
            },
            new GlossaryTerm
            {
                SourceTerm = "结算",
                Description = "电商与订单支付环节统一术语",
                DoNotTranslate = false,
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["en-US"] = "Checkout",
                    ["ja-JP"] = "チェックアウト"
                }
            });

        // 3. 演示项目 shop-vue：虚构的 Vue 商城词条，用于展示占位符、长度约束与「待复核」状态；
        //    自动化测试也以它为固定夹具（真实项目内容会随 MASA.Auth 演进，不适合当断言目标）。
        //    真实业务示例见下方 auth-web-staging / auth-sso-staging。
        var shopProject = new Project
        {
            Code = "shop-vue",
            WriteKey = WriteKeys.Generate(),
            DefaultCulture = "zh-CN",
            SupportedCultures = ["zh-CN", "en-US", "ja-JP"],
            SharedNamespaceCodes = ["common", "errors"],
            Namespaces =
            [
                new TranslationNamespace
                {
                    Code = "order",
                    IsShared = false,
                    Entries =
                    [
                        CreateEntry("submitBtn", "去结算 ({count})", "Checkout ({count})", "チェックアウト ({count})", "购物车底部结算按钮", maxLength: 20),
                        CreateEntry("freeShippingTip", "再买 {amount} 元即可享受免运费", "Add {amount} more for free shipping", "あと{amount}で送料無料になります", "凑单横幅提示"),
                        new TranslationEntry
                        {
                            Key = "refundPolicy",
                            SourceText = "进入工作台可申请 {days} 天无理由退货",
                            Context = "订单详情页底部退货说明",
                            MaxLength = 45,
                            Translations =
                            [
                                new TranslationValue { Culture = "zh-CN", Text = "进入工作台可申请 {days} 天无理由退货", Status = TranslationStatus.Approved },
                                new TranslationValue { Culture = "en-US", Text = "Go to Dashboard to request a return", Status = TranslationStatus.NeedsReview }
                            ]
                        }
                    ]
                }
            ]
        };

        // 4. 内置示例项目：MASA.Auth 的 auth-web-staging / auth-sso-staging（真实服务端错误码词条，编码 = PM 应用 Identity）。
        //    这里只建项目骨架，词条在 SaveChanges 之后走正式导入路径写入（见下方 ImportSeedBundlesAsync）。
        var seedProjects = LinguaSeed.Projects
            .Select(seed => new Project
            {
                Code = seed.Code,
                WriteKey = WriteKeys.Generate(),
                DefaultCulture = seed.DefaultCulture,
                SupportedCultures = [.. seed.SupportedCultures],
                SharedNamespaceCodes = [.. seed.SharedNamespaceCodes]
            })
            .ToList();

        db.Projects.AddRange(shopProject);
        db.Projects.AddRange(seedProjects);
        await db.SaveChangesAsync(ct);

        // 5. 走导入 API 播种真实词条：内置项目必须已存在（导入对不存在的项目直接抛）
        var repository = services.GetRequiredService<ILinguaRepository>();
        await ImportSeedBundlesAsync(repository, ct);
    }

    /// <summary>
    /// 按项目播种内置词条。
    /// <para>
    /// 语种顺序有语义：默认语种必须最先导入，它决定 <c>SourceText</c> 与 <c>SourceTextHash</c>；
    /// 其余语种随后导入时源文案才已知，占位符/长度/术语校验与「源文案已变更」判定才是有效值。
    /// </para>
    /// <para>
    /// 命名空间取 <see cref="LinguaSeed.SeedProject.NamespaceCode"/>：两个 auth 项目为空串（项目根），
    /// 这样发布产物是扁平根 key <c>{"UserNotExist":"…"}</c>，与 MASA I18n 的平铺 key 调用对齐。
    /// </para>
    /// </summary>
    private static async Task ImportSeedBundlesAsync(ILinguaRepository repository, CancellationToken ct)
    {
        foreach (var seed in LinguaSeed.Projects)
        {
            var cultures = seed.SupportedCultures
                .OrderBy(culture => string.Equals(culture, seed.DefaultCulture, StringComparison.OrdinalIgnoreCase) ? 0 : 1);

            foreach (var culture in cultures)
            {
                await repository.ImportJsonBundleAsync(
                    seed.Code,
                    culture,
                    LinguaSeed.ReadBundle(seed.Code, culture),
                    seed.NamespaceCode,
                    ct);
            }
        }
    }

    private static TranslationEntry CreateEntry(
        string key,
        string zhText,
        string enText,
        string jaText,
        string context,
        int? maxLength = null)
    {
        var entry = new TranslationEntry
        {
            Key = key,
            SourceText = zhText,
            Context = context,
            MaxLength = maxLength,
            Translations =
            [
                new TranslationValue { Culture = "zh-CN", Text = zhText, Status = TranslationStatus.Approved },
                new TranslationValue { Culture = "en-US", Text = enText, Status = TranslationStatus.Approved },
                new TranslationValue { Culture = "ja-JP", Text = jaText, Status = TranslationStatus.Approved }
            ]
        };
        return entry;
    }
}
