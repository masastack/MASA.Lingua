using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 词条矩阵每行的「AI 翻译」：「补全缺失」只处理缺失 / 草稿 / 待复核，「重新翻译全部」会连已审核的译文一起覆盖。
/// 后者依赖仓储的写入语义：以 AI 状态写入一条已审核的译文，要替换文本并把状态改成「AI 已译」，不能被「已审核」保护住。
/// </summary>
public sealed class EntryAiRetranslateTests
{
    private static async Task<(IServiceProvider Services, string DbPath)> CreateServicesAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_retranslate");
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();
        return (provider, databaseName);
    }

    private static void Cleanup(string databaseName) => PostgresServer.Drop(databaseName);

    private static async Task SeedProjectAsync(IServiceProvider services, string code)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Projects.Add(new Project
        {
            Code = code,
            DefaultCulture = "zh-CN",
            SupportedCultures = ["zh-CN", "en-US", "ja-JP"],
            SharedNamespaceCodes = []
        });
        await db.SaveChangesAsync();
    }

    private static async Task<TranslationEntry> LoadEntryAsync(ILinguaRepository repo, string code)
    {
        var project = await repo.GetProjectByCodeAsync(code);
        Assert.NotNull(project);
        return project!.Namespaces.Single(n => n.Code == "ns").Entries.Single(e => e.Key == "k");
    }

    [Fact]
    public async Task AiStatusUpsert_Should_Overwrite_An_Approved_Translation_And_Leave_Other_Cultures_Untouched()
    {
        var (services, db) = await CreateServicesAsync();
        try
        {
            await SeedProjectAsync(services, "retranslate-app");
            var repo = services.GetRequiredService<ILinguaRepository>();

            // 三个语种都是人工确认过的「已审核」
            await repo.UpsertEntryAsync(
                "retranslate-app", "ns", "k", "确定", null, null,
                new Dictionary<string, string> { ["zh-CN"] = "确定", ["en-US"] = "OK", ["ja-JP"] = "はい" });
            var before = await LoadEntryAsync(repo, "retranslate-app");
            Assert.All(before.Translations, t => Assert.Equal(TranslationStatus.Approved, t.Status));

            // 「重新翻译全部」只对 en-US 产出新译文时：以 AI 状态写入，覆盖已审核的旧译文
            await repo.UpsertEntryAsync(
                "retranslate-app", "ns", "k", "确定", null, null,
                new Dictionary<string, string> { ["en-US"] = "Confirm" },
                status: TranslationStatus.AiTranslated);

            var after = await LoadEntryAsync(repo, "retranslate-app");
            var en = after.Translations.Single(t => t.Culture == "en-US");
            Assert.Equal("Confirm", en.Text);
            Assert.Equal(TranslationStatus.AiTranslated, en.Status);

            // 没被重新翻译的语种保持原样
            var ja = after.Translations.Single(t => t.Culture == "ja-JP");
            Assert.Equal("はい", ja.Text);
            Assert.Equal(TranslationStatus.Approved, ja.Status);
            var zh = after.Translations.Single(t => t.Culture == "zh-CN");
            Assert.Equal("确定", zh.Text);
            Assert.Equal(TranslationStatus.Approved, zh.Status);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public async Task AiStatusUpsert_Should_Fill_A_Missing_Culture_Without_Touching_Existing_Approved_Ones()
    {
        var (services, db) = await CreateServicesAsync();
        try
        {
            await SeedProjectAsync(services, "retranslate-app");
            var repo = services.GetRequiredService<ILinguaRepository>();

            // ja-JP 缺失：只有 zh-CN 与 en-US
            await repo.UpsertEntryAsync(
                "retranslate-app", "ns", "k", "确定", null, null,
                new Dictionary<string, string> { ["zh-CN"] = "确定", ["en-US"] = "OK" });

            // 「补全缺失」只写入缺失的 ja-JP
            await repo.UpsertEntryAsync(
                "retranslate-app", "ns", "k", "确定", null, null,
                new Dictionary<string, string> { ["ja-JP"] = "確認" },
                status: TranslationStatus.AiTranslated);

            var entry = await LoadEntryAsync(repo, "retranslate-app");
            var ja = entry.Translations.Single(t => t.Culture == "ja-JP");
            Assert.Equal("確認", ja.Text);
            Assert.Equal(TranslationStatus.AiTranslated, ja.Status);

            var en = entry.Translations.Single(t => t.Culture == "en-US");
            Assert.Equal("OK", en.Text);
            Assert.Equal(TranslationStatus.Approved, en.Status);
        }
        finally
        {
            Cleanup(db);
        }
    }
}
