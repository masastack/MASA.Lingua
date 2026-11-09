using System.Globalization;
using System.Net;
using System.Text;
using MASA.Lingua.AI;
using MASA.Lingua.AI.Services;
using MASA.Lingua.SDK.AspNetCore;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Serialization;
using MASA.Lingua.Core.Validation;
using MASA.Lingua.Storage.EfCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MASA.Lingua.Tests;

public sealed class LinguaPlatformTests
{
    [Fact]
    public void JsonBundleConverter_Should_Parse_Nested_And_Flat_Json_And_RoundTrip()
    {
        var nestedVueJson = """
        {
          "common": {
            "ok": "确定",
            "cancel": "取消"
          },
          "order": {
            "btn": {
              "submit": "提交订单 ({count})"
            }
          }
        }
        """;

        var parsed = JsonBundleConverter.ParseExistingJson(Encoding.UTF8.GetBytes(nestedVueJson));
        Assert.Equal(3, parsed.Count);
        Assert.Contains(parsed, x => x.Namespace == "common" && x.Key == "ok" && x.Value == "确定");
        Assert.Contains(parsed, x => x.Namespace == "order" && x.Key == "btn.submit" && x.Value == "提交订单 ({count})");

        var reSerializedBytes = JsonBundleConverter.ToNestedJsonUtf8Bytes(parsed);
        var lookup = JsonBundleConverter.FlattenToLookupDictionary(reSerializedBytes);

        // 同时支持 Vue 的 "." 与 .NET 的 ":" 路径查找
        Assert.Equal("提交订单 ({count})", lookup["order.btn.submit"]);
        Assert.Equal("提交订单 ({count})", lookup["order:btn:submit"]);
        Assert.Equal("确定", lookup["common.ok"]);

        // 项目根（空命名空间）：顶层扁平 Key 发布为 JSON 根级裸 Key，客户端按裸 Key 直查，无任何前缀
        var flatJson = """{"RoleNotExist":"当前角色不存在","UserFrozen":"当前用户已被冻结"}""";
        var flatParsed = JsonBundleConverter.ParseExistingJson(Encoding.UTF8.GetBytes(flatJson)); // defaultNamespace 缺省 = 项目根
        Assert.All(flatParsed, x => Assert.Equal("", x.Namespace));
        var flatLookup = JsonBundleConverter.FlattenToLookupDictionary(JsonBundleConverter.ToNestedJsonUtf8Bytes(flatParsed));
        Assert.Equal("当前角色不存在", flatLookup["RoleNotExist"]);
        Assert.Equal("当前用户已被冻结", flatLookup["UserFrozen"]);
    }

    [Fact]
    public void BundleCompiler_Should_Merge_Shared_Namespace_Fallback_And_Project_Override()
    {
        var compiler = new BundleCompiler();

        var layers = new[]
        {
            // 1. 公共共享命名空间默认语种 (zh-CN)
            new TranslationLayer("Shared:zh-CN", [
                new TranslationItem("common", "ok", "确定"),
                new TranslationItem("common", "onlyInZh", "仅中文提示")
            ]),
            // 2. 公共共享命名空间目标语种 (en-US)
            new TranslationLayer("Shared:en-US", [
                new TranslationItem("common", "ok", "OK")
            ]),
            // 3. 子项目覆盖公共命名空间同名 Key，并定义自有命名空间
            new TranslationLayer("Project:en-US", [
                new TranslationItem("common", "ok", "Confirm Order"),
                new TranslationItem("order", "pay", "Pay Now")
            ])
        };

        var bundle = compiler.Compile("shop-vue", "en-US", layers);

        Assert.StartsWith("\"lingua-", bundle.ETag);
        Assert.Equal("Confirm Order", bundle.LookupEntries["common.ok"]); // 子项目 Override 公共词条
        Assert.Equal("仅中文提示", bundle.LookupEntries["common.onlyInZh"]); // 未翻译词条自动回退到默认语言
        Assert.Equal("Pay Now", bundle.LookupEntries["order.pay"]);
    }

    [Fact]
    public void DeterministicValidator_Should_Catch_Missing_Placeholders_MaxLength_And_Glossary_Violations()
    {
        var validator = new DeterministicTranslationValidator();
        var entry = new TranslationEntry
        {
            Namespace = new TranslationNamespace { Code = "order" },
            Key = "refundTip",
            SourceText = "进入 Lingua 工作台可申请 {days} 天无理由退货",
            MaxLength = 30
        };

        var glossary = new List<GlossaryTerm>
        {
            new()
            {
                SourceTerm = "Lingua",
                DoNotTranslate = true
            },
            new()
            {
                SourceTerm = "工作台",
                DoNotTranslate = false,
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Workspace" }
            }
        };

        // 故意构造：1) 遗漏 {days} 占位符；2) 超出 30 字符限制；3) 未保留 Lingua 品牌名；4) 未将“工作台”译为 Workspace
        var badTranslation = "Go to the system dashboard to apply for a hassle-free return policy now";
        var issues = validator.ValidateEntry(entry, "en-US", badTranslation, glossary);

        Assert.Contains(issues, i => i.RuleCode == "PLACEHOLDER_MISSING");
        Assert.Contains(issues, i => i.RuleCode == "MAX_LENGTH_EXCEEDED");
        Assert.Contains(issues, i => i.RuleCode == "GLOSSARY_DNT_VIOLATION");
        Assert.Contains(issues, i => i.RuleCode == "GLOSSARY_TERM_MISMATCH");
    }

    [Fact]
    public async Task EndToEnd_Storage_AiTranslate_And_ThinAdapters_Should_Work_With_Static_Json()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_test");
        var localJsonDir = Path.Combine(Path.GetTempPath(), $"lingua_i18n_{Guid.NewGuid():N}");

        try
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
            var provider = services.BuildServiceProvider();

            await provider.InitializeLinguaDatabaseAsync();
            var repo = provider.GetRequiredService<ILinguaRepository>();

            // 1. 验证导入现有项目的嵌套 JSON 文件
            var existingJson = Encoding.UTF8.GetBytes("""
            {
              "cart": {
                "emptyTip": "购物车是空的，快去选购吧",
                "itemCount": "已选 {count} 件商品"
              }
            }
            """);

            var importRes = await repo.ImportJsonBundleAsync("shop-vue", "zh-CN", existingJson);
            Assert.Equal(2, importRes.TotalParsed);
            Assert.Contains("cart", importRes.AffectedNamespaces);

            // 2. 验证缺失 Key 自动上报入库
            var reportedCount = await repo.RecordMissingKeysAsync(new MissingKeyBatchReport(
                "shop-vue",
                "zh-CN",
                [new MissingKeyReportItem("cart", "checkoutNow", "立即去结算")]));
            Assert.Equal(1, reportedCount);

            // 3. 验证 AI 批量翻译结合 Glossary 术语表 ("结算" -> "Checkout")
            var glossary = await repo.GetGlossaryTermsAsync();
            var project = await repo.GetProjectByCodeAsync("shop-vue");
            Assert.NotNull(project);

            var checkoutEntry = project!.Namespaces
                .SelectMany(n => n.Entries)
                .First(e => e.Namespace?.Code == "cart" && e.Key == "checkoutNow");

            // 用脚本化的假模型代替真实服务：这里验证「词条 + 术语库 → 提示词 → 译文 → 校验」这条链路的装配，不涉及翻译质量
            var chat = new ScriptedChatClient($$"""{"{{checkoutEntry.Id}}": "Proceed to Checkout now"}""");
            var aiService = new AiTranslationService(
                new DeterministicTranslationValidator(), NullLogger<AiTranslationService>.Instance, chat,
                Options.Create(new LinguaAiOptions()));

            var aiResults = await aiService.TranslateBatchAsync(
                "shop-vue",
                "zh-CN",
                ["en-US"],
                [new AiTranslationRequestItem(checkoutEntry.Id, checkoutEntry.Namespace!.Code, checkoutEntry.Key, checkoutEntry.SourceText, checkoutEntry.Context, checkoutEntry.MaxLength)],
                glossary);

            Assert.Single(aiResults);
            Assert.Contains("Checkout", aiResults[0].TranslatedText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(aiResults[0].ValidationIssues, i => i.IsBlocking);
            // 术语库里的「结算 → Checkout」要被注入到发给模型的提示词里
            Assert.Contains("MUST translate as \"Checkout\"", Assert.Single(chat.Prompts));

            // 4. 编译生成物理静态文件 {culture}.json 到业务项目本地目录，验证 ASP.NET Core / Blazor IStringLocalizer 薄适配器读取与命名占位符格式化
            Directory.CreateDirectory(localJsonDir);
            var compiler = new BundleCompiler();
            var zhLayers = await repo.BuildTranslationLayersAsync("shop-vue", "zh-CN");
            var zhCompiled = compiler.Compile("shop-vue", "zh-CN", zhLayers);

            await File.WriteAllBytesAsync(Path.Combine(localJsonDir, "zh-CN.json"), zhCompiled.NestedJsonUtf8Bytes.ToArray());

            var memoryStore = new LinguaFrozenMemoryStore();
            memoryStore.LoadFromLocalDirectory(localJsonDir);

            var localizer = new LinguaStringLocalizer(memoryStore);
            CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");

            // 验证公共命名空间继承词条 + 命名占位符插值
            var welcome = localizer["common.welcomeUser", new { userName = "张工" }];
            Assert.False(welcome.ResourceNotFound);
            Assert.Equal("欢迎回来，张工！", welcome.Value);

            // 验证导入的嵌套 JSON 词条 + 位置参数插值
            var cartCount = localizer["cart:itemCount", 5];
            Assert.False(cartCount.ResourceNotFound);
            Assert.Equal("已选 5 件商品", cartCount.Value);
        }
        finally
        {
            PostgresServer.Drop(databaseName);
            if (Directory.Exists(localJsonDir))
            {
                try { Directory.Delete(localJsonDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ServerEndpoints_Should_Serve_Fixed_CultureJson_With_ETag_304_And_Accept_Telemetry()
    {
        await using var factory = new IsolatedLinguaFactory(); // 独立数据库与发布目录，不读写开发库
        using var client = factory.CreateClient();

        // 1. 首次请求固定名静态文件 /i18n/shop-vue/zh-CN.json，应返回 200 OK + ETag
        using var firstRes = await client.GetAsync("/i18n/shop-vue/zh-CN.json");
        Assert.Equal(HttpStatusCode.OK, firstRes.StatusCode);

        var etag = firstRes.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag));

        var jsonText = await firstRes.Content.ReadAsStringAsync();
        Assert.Contains("确定", jsonText); // 来自继承的公共命名空间 common.ok
        Assert.Contains("去结算 ({count})", jsonText); // 来自项目自有命名空间 order.submitBtn

        // 2. 第二次请求携带 If-None-Match: ETag，由于后台未重新发布，必须返回 304 Not Modified (0 字节传输)
        using var secondReq = new HttpRequestMessage(HttpMethod.Get, "/i18n/shop-vue/zh-CN.json");
        secondReq.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var secondRes = await client.SendAsync(secondReq);
        Assert.Equal(HttpStatusCode.NotModified, secondRes.StatusCode);
    }

    [Fact]
    public void PostgreSql_Should_Generate_Valid_DDL()
    {
        var optionsBuilder = new DbContextOptionsBuilder<LinguaDbContext>();
        LinguaStorageExtensions.ConfigureDbContextOptions(
            optionsBuilder,
            "Host=127.0.0.1;Port=5432;Database=lingua;Username=postgres;Password=pwd");

        using var db = new LinguaDbContext(optionsBuilder.Options);
        var ddl = db.Database.GenerateCreateScript();

        // 表名不带项目前缀，且语义与实体一一对应
        Assert.Contains("CREATE TABLE projects", ddl);
        Assert.Contains("CREATE TABLE translation_namespaces", ddl);
        Assert.Contains("CREATE TABLE translation_entries", ddl);
        Assert.Contains("CREATE TABLE translation_values", ddl);
        Assert.Contains("CREATE TABLE glossary_terms", ddl);
        Assert.Contains("CREATE TABLE publish_snapshots", ddl);
        Assert.DoesNotContain("lingua_", ddl);

        // JSON 走原生 jsonb；作用域唯一性用部分唯一索引拆开（目标库 PG 14 没有 NULLS NOT DISTINCT）
        Assert.Contains("publish_policy jsonb", ddl);
        Assert.Contains("WHERE project_id IS NULL", ddl);
        Assert.Contains("WHERE project_id IS NOT NULL", ddl);

        // 行版本并发令牌用 PostgreSQL 系统列 xmin（系统列不出现在 CREATE TABLE 里，只能在模型上断言）
        var projectType = db.Model.FindEntityType(typeof(Project));
        Assert.NotNull(projectType);
        var rowVersion = Assert.Single(projectType!.GetProperties(), p => p.IsConcurrencyToken);
        Assert.Equal("xmin", rowVersion.GetColumnName());
        Assert.Equal(typeof(uint), rowVersion.ClrType);
    }

    // ---------------------------------------------------------------------
    // 术语库（Glossary）：作用域、覆盖、去重、匹配、AI 修复与落库状态
    // ---------------------------------------------------------------------

    private static async Task<(ILinguaRepository Repo, string DbPath)> CreateSeededRepositoryAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_glossary");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();
        return (provider.GetRequiredService<ILinguaRepository>(), databaseName);
    }

    private static void CleanupDb(string databaseName) => PostgresServer.Drop(databaseName);

    [Fact]
    public async Task InitializeDatabase_Should_Be_Idempotent()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_idempotent");
        try
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
            var provider = services.BuildServiceProvider();

            await provider.InitializeLinguaDatabaseAsync();
            await provider.InitializeLinguaDatabaseAsync(); // 迁移与种子数据都必须幂等

            var repo = provider.GetRequiredService<ILinguaRepository>();
            Assert.Equal(3, (await repo.GetGlossaryTermsByScopeAsync(null)).Count);
            // 演示项目 shop-vue + 两个内置真实示例（auth-web-staging / auth-sso-staging）
            Assert.Equal(3, (await repo.GetAllProjectsAsync()).Count);
        }
        finally
        {
            CleanupDb(databaseName);
        }
    }

    [Fact]
    public async Task Glossary_ProjectTerm_Should_Override_Global_And_Scope_Queries_Should_Be_Isolated()
    {
        var (repo, dbPath) = await CreateSeededRepositoryAsync();
        try
        {
            var project = await repo.GetProjectByCodeAsync("shop-vue");
            Assert.NotNull(project);

            await repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                ProjectId = project!.Id,
                SourceTerm = "结算",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Settlement" }
            });

            // 生效术语：项目术语完整覆盖同名全局术语，其他全局术语继续继承
            var effective = await repo.GetGlossaryTermsAsync(project.Id);
            var settle = Assert.Single(effective, t => t.SourceTerm == "结算");
            Assert.Equal(project.Id, settle.ProjectId);
            Assert.Equal("Settlement", settle.TargetTranslations["en-US"]);
            Assert.DoesNotContain("ja-JP", settle.TargetTranslations.Keys);
            Assert.Contains(effective, t => t.SourceTerm == "工作台");

            // 不带项目：仅全局术语，仍是 Checkout
            var global = await repo.GetGlossaryTermsAsync();
            Assert.Equal("Checkout", Assert.Single(global, t => t.SourceTerm == "结算").TargetTranslations["en-US"]);

            // 管理页按作用域取原始数据：互不混入
            var projectOnly = await repo.GetGlossaryTermsByScopeAsync(project.Id);
            Assert.Single(projectOnly);
            var globalOnly = await repo.GetGlossaryTermsByScopeAsync(null);
            Assert.Equal(3, globalOnly.Count);
            Assert.All(globalOnly, t => Assert.Null(t.ProjectId));
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task Glossary_Save_Should_Reject_Duplicates_And_Empty_Targets_And_Support_Update()
    {
        var (repo, dbPath) = await CreateSeededRepositoryAsync();
        try
        {
            // 同一作用域内同名（忽略大小写与首尾空白）术语被拒绝
            await Assert.ThrowsAsync<GlossaryValidationException>(() => repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = "  lingua ",
                DoNotTranslate = true
            }));

            // 非 DNT 且没有任何目标译法 = 不产生约束，必须拒绝
            await Assert.ThrowsAsync<GlossaryValidationException>(() => repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = "购物车",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "   " }
            }));

            await Assert.ThrowsAsync<GlossaryValidationException>(() => repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = " ",
                DoNotTranslate = true
            }));

            // 同名术语在不同作用域（项目）下允许存在
            var project = await repo.GetProjectByCodeAsync("shop-vue");
            await repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                ProjectId = project!.Id,
                SourceTerm = "Lingua",
                DoNotTranslate = true
            });

            // 编辑：沿用 Id 更新而不是新增，并且不会被自身判重
            var existing = (await repo.GetGlossaryTermsByScopeAsync(null)).First(t => t.SourceTerm == "工作台");
            await repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                Id = existing.Id,
                SourceTerm = "工作台",
                CaseSensitive = true,
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["en-US"] = "Workbench",
                    ["fr-FR"] = "Espace de travail"
                }
            });

            var globalOnly = await repo.GetGlossaryTermsByScopeAsync(null);
            Assert.Equal(3, globalOnly.Count);
            var updated = Assert.Single(globalOnly, t => t.Id == existing.Id);
            Assert.True(updated.CaseSensitive);
            Assert.Equal("Workbench", updated.TargetTranslations["en-US"]);
            Assert.Equal("Espace de travail", updated.TargetTranslations["fr-FR"]);
            Assert.DoesNotContain("ja-JP", updated.TargetTranslations.Keys);

            // DNT 术语保存时清空目标译法
            await repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = "Blazor",
                DoNotTranslate = true,
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "ignored" }
            });
            var blazor = (await repo.GetGlossaryTermsByScopeAsync(null)).Single(t => t.SourceTerm == "Blazor");
            Assert.Empty(blazor.TargetTranslations);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task UpsertEntry_Status_Should_Be_Persisted_And_Default_Behavior_Should_Not_Regress()
    {
        var (repo, dbPath) = await CreateSeededRepositoryAsync();
        try
        {
            // 未指定 status：新建译文保持原有默认 Approved
            await repo.UpsertEntryAsync("shop-vue", "order", "gate1", "源文案", null, null,
                new Dictionary<string, string> { ["en-US"] = "Source" });

            // 指定 status：AI 含阻断问题的译文进入待复核；已有译文更新状态
            await repo.UpsertEntryAsync("shop-vue", "order", "gate1", "源文案", null, null,
                new Dictionary<string, string> { ["en-US"] = "AI text", ["ja-JP"] = "AI テキスト" },
                status: TranslationStatus.NeedsReview);

            // 之后不带 status 的更新不改变已有状态
            await repo.UpsertEntryAsync("shop-vue", "order", "gate1", "源文案", null, null,
                new Dictionary<string, string> { ["ja-JP"] = "AI テキスト2" });

            var project = await repo.GetProjectByCodeAsync("shop-vue");
            var entry = project!.Namespaces.SelectMany(n => n.Entries).Single(e => e.Key == "gate1");
            var en = entry.Translations.Single(t => t.Culture == "en-US");
            var ja = entry.Translations.Single(t => t.Culture == "ja-JP");
            Assert.Equal("AI text", en.Text);
            Assert.Equal(TranslationStatus.NeedsReview, en.Status);
            Assert.Equal("AI テキスト2", ja.Text);
            Assert.Equal(TranslationStatus.NeedsReview, ja.Status);

            // 人工编辑显式标记 Approved
            await repo.UpsertEntryAsync("shop-vue", "order", "gate1", "源文案", null, null,
                new Dictionary<string, string> { ["en-US"] = "Human text" },
                status: TranslationStatus.Approved);
            project = await repo.GetProjectByCodeAsync("shop-vue");
            entry = project!.Namespaces.SelectMany(n => n.Entries).Single(e => e.Key == "gate1");
            Assert.Equal(TranslationStatus.Approved, entry.Translations.Single(t => t.Culture == "en-US").Status);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void GlossaryValidator_Should_Use_WordBoundary_LongestMatch_And_CaseSensitivity()
    {
        var validator = new DeterministicTranslationValidator();

        GlossaryTerm Term(string source, string? en, bool dnt = false, bool caseSensitive = false) => new()
        {
            SourceTerm = source,
            DoNotTranslate = dnt,
            CaseSensitive = caseSensitive,
            TargetTranslations = en is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = en }
        };

        TranslationEntry Entry(string source) => new()
        {
            Namespace = new TranslationNamespace { Code = "t" },
            Key = "k",
            SourceText = source
        };

        // 1. 拉丁术语词边界："Lingua" 不应命中 "Linguaphone"，因此译文不含 Lingua 也不报错
        var lingua = Term("Lingua", null, dnt: true);
        Assert.DoesNotContain(
            validator.ValidateEntry(Entry("Linguaphone 商店"), "en-US", "Phone store", [lingua]),
            i => i.RuleCode.StartsWith("GLOSSARY_"));
        Assert.Contains(
            validator.ValidateEntry(Entry("欢迎使用 Lingua 平台"), "en-US", "Welcome to the platform", [lingua]),
            i => i.RuleCode == "GLOSSARY_DNT_VIOLATION");

        // 2. 区分大小写：开启后小写 lingua 不命中；译文大小写也必须一致
        var strict = Term("Lingua", null, dnt: true, caseSensitive: true);
        Assert.DoesNotContain(
            validator.ValidateEntry(Entry("lingua 平台"), "en-US", "platform", [strict]),
            i => i.RuleCode.StartsWith("GLOSSARY_"));
        Assert.Contains(
            validator.ValidateEntry(Entry("Lingua 平台"), "en-US", "lingua platform", [strict]),
            i => i.RuleCode == "GLOSSARY_DNT_VIOLATION");
        // 默认不区分大小写时，源侧与译侧一致地忽略大小写
        Assert.DoesNotContain(
            validator.ValidateEntry(Entry("Lingua 平台"), "en-US", "lingua platform", [lingua]),
            i => i.RuleCode.StartsWith("GLOSSARY_"));

        // 3. 最长匹配优先：术语表同时存在「结算」与「未结算」时，「未结算」处不再要求出现 Checkout
        var checkout = Term("结算", "Checkout");
        var unsettled = Term("未结算", "Unsettled");
        var okIssues = validator.ValidateEntry(Entry("订单未结算"), "en-US", "Order unsettled", [checkout, unsettled]);
        Assert.DoesNotContain(okIssues, i => i.RuleCode.StartsWith("GLOSSARY_"));

        // 同一句里另有独立的「结算」时仍需约束
        var mixed = validator.ValidateEntry(Entry("订单未结算，请去结算"), "en-US", "Order unsettled, please pay", [checkout, unsettled]);
        Assert.Contains(mixed, i => i.RuleCode == "GLOSSARY_TERM_MISMATCH" && i.SuggestedFix == "Checkout");

        // 4. 英文复数容忍：目标侧不加词边界
        var workspace = Term("工作台", "Workspace");
        Assert.DoesNotContain(
            validator.ValidateEntry(Entry("管理多个工作台"), "en-US", "Manage multiple Workspaces", [workspace]),
            i => i.RuleCode.StartsWith("GLOSSARY_"));

        // 5. 术语违规一律视为阻断问题（包括 Warning 级的标准译法不一致）
        var mismatch = validator.ValidateEntry(Entry("进入工作台"), "en-US", "Go to Dashboard", [workspace]);
        var issue = Assert.Single(mismatch, i => i.RuleCode == "GLOSSARY_TERM_MISMATCH");
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.True(issue.IsBlocking);
        Assert.False(new ValidationIssue("t.k", "en-US", "EMPTY_TRANSLATION", IssueSeverity.Warning, "x").IsBlocking);
    }

    [Fact]
    public async Task AiTranslation_Should_Only_Inject_Relevant_Glossary_And_Repair_Blocking_Violations_Once()
    {
        var entryId = Guid.NewGuid();
        var item = new AiTranslationRequestItem(
            entryId, "order", "refundPolicy", "进入工作台可申请 {days} 天无理由退货", "订单详情页底部退货说明", 60);

        var glossary = new List<GlossaryTerm>
        {
            new()
            {
                SourceTerm = "工作台",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Workspace" }
            },
            new()
            {
                SourceTerm = "结算",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Checkout" }
            }
        };

        // 第一次：缺占位符 + 未使用标准译法；第二次（修复重试）：返回合规译文
        var client = new ScriptedChatClient(
            $$"""{"{{entryId}}": "Go to Dashboard to request a return"}""",
            $$"""{"{{entryId}}": "Go to Workspace to request a {days}-day return"}""");

        var service = new AiTranslationService(
            new DeterministicTranslationValidator(),
            NullLogger<AiTranslationService>.Instance,
            client,
            Options.Create(new LinguaAiOptions()));

        var results = await service.TranslateBatchAsync("shop-vue", "zh-CN", ["en-US"], [item], glossary);

        var result = Assert.Single(results);
        Assert.Equal("Go to Workspace to request a {days}-day return", result.TranslatedText);
        Assert.DoesNotContain(result.ValidationIssues, i => i.IsBlocking);

        // 恰好两次调用：首次翻译 + 一次修复重试
        Assert.Equal(2, client.Prompts.Count);

        // 只注入本批源文案命中的术语：「结算」未出现在源文案中，不应出现在 Prompt 里
        Assert.Contains("工作台", client.Prompts[0]);
        Assert.DoesNotContain("结算", client.Prompts[0]);
        Assert.Contains("工作台", client.Prompts[1]);
        Assert.DoesNotContain("结算", client.Prompts[1]);
        // 修复 Prompt 带上具体问题与建议修复值
        Assert.Contains("previousTranslation", client.Prompts[1]);
        Assert.Contains("Workspace", client.Prompts[1]);
    }

    [Fact]
    public async Task AiTranslation_Should_Keep_Original_And_Report_Blocking_When_Repair_Does_Not_Help()
    {
        var entryId = Guid.NewGuid();
        var item = new AiTranslationRequestItem(entryId, "order", "k", "进入工作台", null, null);
        var glossary = new List<GlossaryTerm>
        {
            new()
            {
                SourceTerm = "工作台",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Workspace" }
            }
        };

        var client = new ScriptedChatClient($$"""{"{{entryId}}": "Go to Dashboard"}""");
        var service = new AiTranslationService(
            new DeterministicTranslationValidator(),
            NullLogger<AiTranslationService>.Instance,
            client,
            Options.Create(new LinguaAiOptions()));

        var result = Assert.Single(await service.TranslateBatchAsync("shop-vue", "zh-CN", ["en-US"], [item], glossary));

        // 修复重试仍违规：只重试一次，保留原译文，并把阻断问题交给调用方（调用方据此标记为待复核）
        Assert.Equal(2, client.Prompts.Count);
        Assert.Equal("Go to Dashboard", result.TranslatedText);
        Assert.Contains(result.ValidationIssues, i => i.IsBlocking && i.RuleCode == "GLOSSARY_TERM_MISMATCH");
    }

    // ---------------------------------------------------------------------
    // 阶段 0：语种规范化、版本指纹、源文案变更置待复核、导入校验
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("en-us", "en-US")]
    [InlineData("zh_CN", "zh-CN")]
    [InlineData(" JA-jp ", "ja-JP")]
    [InlineData("zh-hant-tw", "zh-Hant-TW")]
    public void LinguaCultures_Should_Normalize_To_Bcp47(string input, string expected)
    {
        Assert.Equal(expected, Core.Localization.LinguaCultures.Normalize(input));
    }

    [Fact]
    public void LinguaCultures_Resolve_Should_Reject_Garbage_And_Map_To_Existing_Project_Culture()
    {
        Assert.Throws<Core.Localization.LinguaValidationException>(() => Core.Localization.LinguaCultures.Resolve("../etc"));
        Assert.Throws<Core.Localization.LinguaValidationException>(() => Core.Localization.LinguaCultures.Resolve("xx-YY"));
        Assert.Throws<Core.Localization.LinguaValidationException>(() => Core.Localization.LinguaCultures.Resolve(""));

        // 项目已有的写法优先；语种目录内的新语种可以通过
        Assert.Equal("en-US", Core.Localization.LinguaCultures.Resolve("en-us", ["zh-CN", "en-US"]));
        Assert.Equal("fr-FR", Core.Localization.LinguaCultures.Resolve("fr_fr", ["zh-CN"]));
    }

    [Fact]
    public void LinguaCultures_Catalog_Should_Come_From_Icu_With_Stable_Ordering()
    {
        var catalog = Core.Localization.LinguaCultures.Catalog;

        // 目录来自 ICU，没有任何人工清单
        Assert.True(catalog.Count > 200, $"语种目录只有 {catalog.Count} 项，ICU 目录没有接上");
        Assert.Contains(catalog, c => c.Code == "ko-KR");
        Assert.Contains(catalog, c => c.Code == "af-ZA");

        // 按语种代码排序：几百项的下拉必须有可预测的顺序，同一语言的不同地区变体相邻
        var codes = catalog.Select(c => c.Code).ToList();
        Assert.Equal(codes.OrderBy(c => c, StringComparer.Ordinal).ToList(), codes);

        // 目录里不能有本类自己解析不了的历史代码（如 zh-CHS）：选中即报错的项不该出现在下拉里
        Assert.All(catalog, c => Assert.Equal(c.Code, Core.Localization.LinguaCultures.Normalize(c.Code)));

        // 名称取自 ICU 的 NativeName（不是退回代码）
        Assert.NotEqual("ko-KR", Core.Localization.LinguaCultures.GetName("ko-KR"));
        // 不认识的代码原样返回，不抛异常（历史数据里可能存着已弃用的代码）
        Assert.Equal("zz-ZZ", Core.Localization.LinguaCultures.GetName("zz-ZZ"));
    }

    /// <summary>
    /// 校验的判据是「ICU 认不认识这个预定义文化」，与目录枚举结果无关。
    /// 这条测试钉住两个方向：枚举不到的真实代码必须放行（zh-CN 是本平台默认源语种），
    /// 能凭空合成出来的假代码必须继续拒绝。
    /// </summary>
    [Theory]
    [InlineData("zh-CN", true)]     // ICU 不枚举（规范成 zh-Hans-CN），但它是真实预定义文化
    [InlineData("zh-TW", true)]
    [InlineData("zh-HK", true)]
    [InlineData("zh-Hans-CN", true)] // ICU 枚举结果里的规范写法
    [InlineData("en-US", true)]
    [InlineData("iw", true)]        // 旧别名，ICU 认
    [InlineData("xx-YY", false)]    // 可合成但非预定义：必须拒绝
    [InlineData("zz-ZZ", false)]
    [InlineData("zh-CHS", false)]   // 不符合 BCP47 脚本段写法
    public void LinguaCultures_IsKnownCulture_Should_Follow_Icu_Not_Enumeration(string code, bool expected)
    {
        Assert.Equal(expected, Core.Localization.LinguaCultures.IsKnownCulture(code));

        if (expected)
        {
            Assert.Equal(code, Core.Localization.LinguaCultures.Resolve(code, ["en-US"]));
        }
        else
        {
            Assert.Throws<Core.Localization.LinguaValidationException>(
                () => Core.Localization.LinguaCultures.Resolve(code, ["en-US"]));
        }
    }

    /// <summary>
    /// 默认语种必须永远可选：它是词条源文案语种，也是新项目的初始值。
    /// ICU 的枚举结果里没有 zh-CN，所以下拉候选必须靠 <c>WithExtraCodes</c> 补进来，而不是靠硬编码清单。
    /// </summary>
    [Fact]
    public void LinguaCultures_WithExtraCodes_Should_Make_InUse_Cultures_Selectable()
    {
        var defaultCulture = Core.Localization.LinguaCultures.DefaultSourceCulture;
        var withDefault = Core.Localization.LinguaCultures.WithExtraCodes([defaultCulture]);

        Assert.Contains(withDefault, c => c.Code == defaultCulture);
        Assert.Equal(withDefault.Select(c => c.Code).OrderBy(c => c, StringComparer.Ordinal),
            withDefault.Select(c => c.Code));

        // 假代码补不进去（显示层与校验层同一判据）
        var withFake = Core.Localization.LinguaCultures.WithExtraCodes(["xx-YY"]);
        Assert.DoesNotContain(withFake, c => c.Code == "xx-YY");

        // 已存在的代码不会重复
        Assert.Single(withDefault, c => c.Code == "ko-KR");
    }

    /// <summary>
    /// 语种目录的搜索字段：中文名（韩语）、英文名（Korean）、本地名（한국어）、代码（ko-KR）都要能用。
    /// 中文名尤其重要——它来自 ICU 的 DisplayName，随 CurrentUICulture 变化，
    /// 若按容器区域设置取（常见为 en-US）就会变成 "Korean (Korea)"，中文用户搜「韩语」会一无所获。
    /// </summary>
    [Fact]
    public void LinguaCultures_Options_Should_Carry_Searchable_Names_Including_Chinese()
    {
        var ko = Core.Localization.LinguaCultures.Catalog.First(c => c.Code == "ko-KR");

        Assert.Equal("한국어(대한민국)", ko.NativeName);
        Assert.Contains("Korean", ko.EnglishName, StringComparison.Ordinal);
        Assert.Contains("韩语", ko.ChineseName, StringComparison.Ordinal);

        // 与 CurrentUICulture 无关：即使当前线程是英文环境，中文名仍是中文
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
            var underEnglish = Core.Localization.LinguaCultures.Catalog.First(c => c.Code == "ko-KR");
            Assert.Contains("韩语", underEnglish.ChineseName, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void LinguaCultures_Resolve_Should_Accept_Every_Catalog_Culture()
    {
        // 下拉里能选到的语种，Resolve 必须都能收（否则「选中即保存」会在界面上直接报错）
        foreach (var culture in Core.Localization.LinguaCultures.Catalog)
        {
            Assert.Equal(culture.Code, Core.Localization.LinguaCultures.Resolve(culture.Code, ["en-US"]));
        }

        // 同理：WithExtraCodes 补进来的项也必须都能被收下
        foreach (var culture in Core.Localization.LinguaCultures.WithExtraCodes(
                     [Core.Localization.LinguaCultures.DefaultSourceCulture]))
        {
            Assert.Equal(culture.Code, Core.Localization.LinguaCultures.Resolve(culture.Code, ["en-US"]));
        }
    }

    [Fact]
    public void BundleCompiler_VersionHash_Should_Cover_All_Cultures_And_Published_Bytes_Should_Keep_ETag()
    {
        var compiler = new BundleCompiler();

        var a = BundleCompiler.ComputeVersionHash([("zh-CN", "aaaa"), ("en-US", "bbbb")]);
        var reordered = BundleCompiler.ComputeVersionHash([("en-US", "bbbb"), ("zh-CN", "aaaa")]);
        var changedSecond = BundleCompiler.ComputeVersionHash([("zh-CN", "aaaa"), ("en-US", "cccc")]);

        Assert.Equal(a, reordered);          // 与语种顺序无关
        Assert.NotEqual(a, changedSecond);   // 非首个语种变化，指纹也会变化（旧实现只取首个语种）
        Assert.Equal("initial", BundleCompiler.ComputeVersionHash([]));

        var compiled = compiler.Compile("p", "zh-CN", [new TranslationLayer("Project:zh-CN", [new TranslationItem("common", "ok", "确定")])]);
        var restored = compiler.FromPublishedBytes("p", "zh-CN", compiled.NestedJsonUtf8Bytes.ToArray());
        Assert.Equal(compiled.ETag, restored.ETag);
        Assert.Equal("确定", restored.LookupEntries["common.ok"]);
    }

    [Fact]
    public async Task SourceTextChange_Should_Mark_Other_Cultures_NeedsReview_And_Reconfirm_Should_Clear()
    {
        var (repo, dbPath) = await CreateSeededRepositoryAsync();
        try
        {
            await repo.UpsertEntryAsync("shop-vue", "order", "stale1", "支付 {amount} 元", null, null,
                new Dictionary<string, string> { ["zh-CN"] = "支付 {amount} 元", ["en-US"] = "Pay {amount}", ["ja-JP"] = "{amount} 円を支払う" },
                status: TranslationStatus.Approved);
            await repo.UpsertEntryAsync("shop-vue", "order", "stale1", "支付 {amount} 元", null, null,
                new Dictionary<string, string> { ["ja-JP"] = "{amount} 円を支払う" },
                status: TranslationStatus.AiTranslated);

            // 源文案没变：不应置待复核
            var project = await repo.GetProjectByCodeAsync("shop-vue");
            var entry = project!.Namespaces.SelectMany(n => n.Entries).Single(e => e.Key == "stale1");
            Assert.All(entry.Translations, t => Assert.False(t.IsStaleFor(entry)));
            Assert.Equal(TranslationStatus.Approved, entry.Translations.Single(t => t.Culture == "en-US").Status);

            // 改源文案（只带源语种译文）：其他语种的「已审核 / AI 已译」译文全部进入待复核
            await repo.UpsertEntryAsync("shop-vue", "order", "stale1", "立即支付 {amount} 元", null, null,
                new Dictionary<string, string> { ["zh-CN"] = "立即支付 {amount} 元" },
                status: TranslationStatus.Approved);

            project = await repo.GetProjectByCodeAsync("shop-vue");
            entry = project!.Namespaces.SelectMany(n => n.Entries).Single(e => e.Key == "stale1");
            var en = entry.Translations.Single(t => t.Culture == "en-US");
            var ja = entry.Translations.Single(t => t.Culture == "ja-JP");
            var zh = entry.Translations.Single(t => t.Culture == "zh-CN");
            Assert.Equal(TranslationStatus.NeedsReview, en.Status);
            Assert.Equal(TranslationStatus.NeedsReview, ja.Status);
            Assert.True(en.IsStaleFor(entry));
            Assert.Equal(TranslationStatus.Approved, zh.Status);   // 源语种本身不受影响
            Assert.False(zh.IsStaleFor(entry));

            // 人工重新确认（写入译文）后：状态恢复、过时标记清除
            await repo.UpsertEntryAsync("shop-vue", "order", "stale1", "立即支付 {amount} 元", null, null,
                new Dictionary<string, string> { ["en-US"] = "Pay {amount} now" },
                status: TranslationStatus.Approved);
            project = await repo.GetProjectByCodeAsync("shop-vue");
            entry = project!.Namespaces.SelectMany(n => n.Entries).Single(e => e.Key == "stale1");
            en = entry.Translations.Single(t => t.Culture == "en-US");
            Assert.Equal(TranslationStatus.Approved, en.Status);
            Assert.False(en.IsStaleFor(entry));
            Assert.Null(en.ReviewComment);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task ImportJson_Should_Validate_Culture_Run_Rules_And_Mark_Stale()
    {
        var (repo, dbPath) = await CreateSeededRepositoryAsync();
        try
        {
            // 非法 / 未知语种直接拒绝，不会悄悄新增语种
            var json = Encoding.UTF8.GetBytes("""{ "order": { "payTip": "x" } }""");
            await Assert.ThrowsAsync<Core.Localization.LinguaValidationException>(() => repo.ImportJsonBundleAsync("shop-vue", "../../x", json));
            await Assert.ThrowsAsync<Core.Localization.LinguaValidationException>(() => repo.ImportJsonBundleAsync("shop-vue", "xx-YY", json));

            // 先导入源语种
            await repo.ImportJsonBundleAsync("shop-vue", "zh-CN",
                Encoding.UTF8.GetBytes("""{ "order": { "payTip": "请支付 {amount} 元" } }"""));

            // en-us 会映射回已有的 en-US；导入一律按已审核入库，违规仅统计提示（FlaggedIssues），不再降级待复核
            var res = await repo.ImportJsonBundleAsync("shop-vue", "en-us",
                Encoding.UTF8.GetBytes("""{ "order": { "payTip": "Please pay" , "okTip": "Done" } }"""));
            Assert.Equal("en-US", res.Culture);
            Assert.Equal(1, res.FlaggedIssues);

            var project = await repo.GetProjectByCodeAsync("shop-vue");
            Assert.Equal(1, project!.SupportedCultures.Count(c => string.Equals(c, "en-US", StringComparison.OrdinalIgnoreCase)));
            var payTip = project.Namespaces.SelectMany(n => n.Entries).Single(e => e.NamespaceCode == "order" && e.Key == "payTip");
            var enTip = payTip.Translations.Single(t => t.Culture == "en-US");
            Assert.Equal(TranslationStatus.Approved, enTip.Status);
            Assert.Null(enTip.ReviewComment);

            // 再导入新的源语种文案：其他语种译文因源文案变更进入待复核
            await repo.ImportJsonBundleAsync("shop-vue", "en-US",
                Encoding.UTF8.GetBytes("""{ "order": { "payTip": "Please pay {amount}" } }"""));
            var changed = await repo.ImportJsonBundleAsync("shop-vue", "zh-CN",
                Encoding.UTF8.GetBytes("""{ "order": { "payTip": "请尽快支付 {amount} 元" } }"""));
            Assert.Equal(1, changed.StaleMarked);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task Reimport_Same_Json_Should_Restore_Review_To_Approved_And_Clear_Stale()
    {
        var (repo, dbPath) = await CreateSeededRepositoryAsync();
        try
        {
            // 先导入源语种与目标语种，再改源文案让 en-US 进入「待复核」（源文案已变更标记）
            await repo.ImportJsonBundleAsync("shop-vue", "zh-CN",
                Encoding.UTF8.GetBytes("""{ "order": { "payTip": "请支付 {amount} 元" } }"""));
            await repo.ImportJsonBundleAsync("shop-vue", "en-US",
                Encoding.UTF8.GetBytes("""{ "order": { "payTip": "Please pay {amount}" } }"""));
            await repo.ImportJsonBundleAsync("shop-vue", "zh-CN",
                Encoding.UTF8.GetBytes("""{ "order": { "payTip": "请尽快支付 {amount} 元" } }"""));

            async Task<TranslationEntry> EntryOf() => (await repo.GetProjectByCodeAsync("shop-vue"))!
                .Namespaces.SelectMany(n => n.Entries).Single(e => e.NamespaceCode == "order" && e.Key == "payTip");
            Assert.Equal(TranslationStatus.NeedsReview, (await EntryOf()).Translations.Single(t => t.Culture == "en-US").Status);

            // 重新导入同一份 en-US JSON：文本未变的行也更新状态 / 备注 / hash——
            // 批量恢复为已审核、清空待复核原因、清除「源文案已变更」标记；更新计数只算文本真实变化的行
            var res = await repo.ImportJsonBundleAsync("shop-vue", "en-US",
                Encoding.UTF8.GetBytes("""{ "order": { "payTip": "Please pay {amount}" } }"""));
            Assert.Equal(0, res.UpdatedTranslations);

            var entry = await EntryOf();
            var enTip = entry.Translations.Single(t => t.Culture == "en-US");
            Assert.Equal(TranslationStatus.Approved, enTip.Status);
            Assert.Null(enTip.ReviewComment);
            Assert.False(enTip.IsStaleFor(entry));
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task ServerEndpoints_Should_Not_Serve_Unknown_Cultures_Or_Unpublished_Drafts()
    {
        await using var factory = new IsolatedLinguaFactory(); // 独立数据库与发布目录，不读写开发库
        using var client = factory.CreateClient();

        using var unknownCulture = await client.GetAsync("/i18n/shop-vue/xx-YY.json");
        Assert.Equal(HttpStatusCode.NotFound, unknownCulture.StatusCode);

        using var garbageCulture = await client.GetAsync("/api/v1/bundles/shop-vue/not-a-culture");
        Assert.Equal(HttpStatusCode.NotFound, garbageCulture.StatusCode);

        using var unknownProject = await client.GetAsync("/api/v1/bundles/nope/zh-CN");
        Assert.Equal(HttpStatusCode.NotFound, unknownProject.StatusCode);

        // 导入接口：非法语种 400
        using var badImport = await client.PostAsync(
            "/api/v1/projects/shop-vue/import-json?culture=bad_culture!",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badImport.StatusCode);
    }
}
