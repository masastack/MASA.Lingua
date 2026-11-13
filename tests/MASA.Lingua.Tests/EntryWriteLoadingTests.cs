using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 词条写入路径「只加载涉及的行」：编辑一个词条、批量写几个词条、缺词上报几个 Key，
/// 都不能把整个命名空间（上万词条）读进内存。断言落在实际发给库的 SQL 上（每条查词条的语句必须带 Key 过滤），
/// 同时验证行为仍与全量加载时一致（命中就地更新、未命中新增、旁边的词条不受影响）。
/// </summary>
public sealed class EntryWriteLoadingTests
{
    private const int BulkCount = 300;

    /// <summary>用正式的导入路径播种：默认语种建词条，en-US 补译文。key = k00000…</summary>
    internal static async Task SeedBulkAsync(ILinguaRepository repo, string projectCode, string namespaceCode, int count)
    {
        await repo.CreateProjectAsync(projectCode);
        await repo.ImportJsonBundleAsync(projectCode, "zh-CN", Bundle(count, i => $"源文案 {i}"), namespaceCode);
        await repo.ImportJsonBundleAsync(projectCode, "en-US", Bundle(count, i => $"Text {i}"), namespaceCode);
    }

    private static byte[] Bundle(int count, Func<int, string> text)
    {
        var map = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            map[$"k{i:D5}"] = text(i);
        }

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(map));
    }

    /// <summary>
    /// 所有查词条表的语句都带 Key 条件（单个 Key 或 ANY 数组）：没有「读整个命名空间」的查询。
    /// Key 忽略大小写，条件落在 <c>lower(key)</c> 上（命中表达式唯一索引），所以匹配 <c>key = </c> 或 <c>lower(...key) = </c>。
    /// </summary>
    private static void AssertEntryReadsAreKeyFiltered(IReadOnlyList<string> commands)
    {
        var entryReads = commands.Where(c => c.Contains("FROM translation_entries", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(entryReads); // 防止没读词条表时空过
        Assert.All(entryReads, sql => Assert.Matches(new Regex(@"\blower\([\w\.""]*\bkey\) = "), sql));
    }

    [Fact]
    public async Task UpsertEntry_On_Existing_Key_Updates_In_Place_And_Loads_Only_That_Entry()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_single_upd");
        await SeedBulkAsync(db.Repo, "bulk-proj", "bulk", BulkCount);
        var probe = db.NewProbed();

        probe.Commands.Clear();
        await probe.Repo.UpsertEntryAsync(
            "bulk-proj", "bulk", "k00150", "改后的源文案", null, null,
            new Dictionary<string, string> { ["en-US"] = "Edited" },
            status: TranslationStatus.Approved);
        AssertEntryReadsAreKeyFiltered(probe.Commands.Commands);

        var ns = (await db.Repo.GetProjectByCodeAsync("bulk-proj"))!.Namespaces.Single(n => n.Code == "bulk");
        Assert.Equal(BulkCount, ns.Entries.Count); // 没有多出、也没有丢

        var edited = ns.Entries.Single(e => e.Key == "k00150");
        Assert.Equal("改后的源文案", edited.SourceText);
        Assert.Equal("Edited", edited.Translations.Single(t => t.Culture == "en-US").Text);

        // 旁边的词条原封不动
        var neighbor = ns.Entries.Single(e => e.Key == "k00151");
        Assert.Equal("源文案 151", neighbor.SourceText);
        Assert.Equal("Text 151", neighbor.Translations.Single(t => t.Culture == "en-US").Text);
    }

    [Fact]
    public async Task UpsertEntry_Creates_New_Entry_And_Namespace_Shell_Without_Loading_Siblings()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_single_new");
        await SeedBulkAsync(db.Repo, "bulk-proj", "bulk", BulkCount);
        var probe = db.NewProbed();

        probe.Commands.Clear();
        await probe.Repo.UpsertEntryAsync("bulk-proj", "bulk", "brandNew", "全新词条", null, null);
        await probe.Repo.UpsertEntryAsync("bulk-proj", "fresh-ns", "first", "新命名空间词条", null, null);
        AssertEntryReadsAreKeyFiltered(probe.Commands.Commands);

        var project = (await db.Repo.GetProjectByCodeAsync("bulk-proj"))!;
        Assert.Equal(BulkCount + 1, project.Namespaces.Single(n => n.Code == "bulk").Entries.Count);
        var fresh = project.Namespaces.Single(n => n.Code == "fresh-ns");
        Assert.Equal("first", Assert.Single(fresh.Entries).Key);
    }

    [Fact]
    public async Task UpsertTranslationsBatch_Loads_Only_The_Batch_Keys_And_Merges_Duplicates()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_batch_keys");
        await SeedBulkAsync(db.Repo, "bulk-proj", "bulk", BulkCount);
        var probe = db.NewProbed();

        TranslationUpsertBatchItem Item(string ns, string key, string source, string culture, string text) =>
            new(ns, key, source, null, null, new Dictionary<string, string> { [culture] = text }, TranslationStatus.AiTranslated);

        var items = new List<TranslationUpsertBatchItem>
        {
            Item("bulk", "k00010", "源文案 10", "en-US", "Batch updated"),   // 已有词条改译文
            Item("bulk", "k00011", "源文案 11", "ja-JP", "日本語を追加"),      // 已有词条加语种
            Item("bulk", "batchNew", "批量新词条", "en-US", "Batch new"),      // 已有命名空间新增词条
            Item("batch-ns", "inNewNs", "新命名空间", "en-US", "In new ns"),   // 新命名空间
            Item("bulk", "dupNew", "重复 Key", "en-US", "Dup en"),             // 同一批里同一个新 Key 出现两次
            Item("bulk", "dupNew", "重复 Key", "ja-JP", "Dup ja"),
        };

        probe.Commands.Clear();
        await probe.Repo.UpsertTranslationsBatchAsync("bulk-proj", items);
        AssertEntryReadsAreKeyFiltered(probe.Commands.Commands);
        Assert.All(
            probe.Commands.Commands.Where(c => c.Contains("FROM translation_entries", StringComparison.Ordinal)),
            sql => Assert.Contains("ANY", sql)); // 一个命名空间一次查询，用 Key 数组

        var project = (await db.Repo.GetProjectByCodeAsync("bulk-proj"))!;
        var bulk = project.Namespaces.Single(n => n.Code == "bulk");
        Assert.Equal(BulkCount + 2, bulk.Entries.Count); // batchNew + dupNew（重复只建一条）

        Assert.Equal("Batch updated", bulk.Entries.Single(e => e.Key == "k00010").Translations.Single(t => t.Culture == "en-US").Text);
        Assert.Equal("日本語を追加", bulk.Entries.Single(e => e.Key == "k00011").Translations.Single(t => t.Culture == "ja-JP").Text);
        Assert.Equal("Text 11", bulk.Entries.Single(e => e.Key == "k00011").Translations.Single(t => t.Culture == "en-US").Text); // 原有语种保留

        var dup = bulk.Entries.Single(e => e.Key == "dupNew");
        Assert.Equal(["en-US", "ja-JP"], dup.Translations.Select(t => t.Culture).Order());

        Assert.Equal("In new ns", project.Namespaces.Single(n => n.Code == "batch-ns").Entries.Single().Translations.Single().Text);

        // 没被点名的词条不受影响
        Assert.Equal("Text 12", bulk.Entries.Single(e => e.Key == "k00012").Translations.Single(t => t.Culture == "en-US").Text);
    }

    [Fact]
    public async Task RecordMissingKeys_Queries_Only_The_Involved_Namespaces_And_Keys()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_missing_keys");
        await SeedBulkAsync(db.Repo, "bulk-proj", "bulk", BulkCount);
        var probe = db.NewProbed();

        var report = new MissingKeyBatchReport("bulk-proj", "zh-CN",
        [
            new MissingKeyReportItem("bulk", "k00001"),                 // 已存在：不重复创建
            new MissingKeyReportItem("bulk", "missing1", "缺失 1"),
            new MissingKeyReportItem("bulk", "missing1", "缺失 1"),     // 本批内重复：只算一次
            new MissingKeyReportItem("other-ns", "missing2"),            // 新命名空间
            new MissingKeyReportItem("", "rootKey", "项目根词条"),       // 空命名空间 = 项目根
        ]);

        probe.Commands.Clear();
        var created = await probe.Repo.RecordMissingKeysAsync(report);
        Assert.Equal(3, created);

        // 只查涉及的命名空间与 Key（数组参数），不读整个项目
        var selects = probe.Commands.Commands.Where(c => c.StartsWith("SELECT", StringComparison.Ordinal)
                                                         || c.Contains("\nSELECT", StringComparison.Ordinal)).ToList();
        Assert.Contains(selects, sql => sql.Contains("FROM translation_namespaces") && sql.Contains("ANY"));
        AssertEntryReadsAreKeyFiltered(probe.Commands.Commands);
        Assert.DoesNotContain(probe.Commands.Commands, sql => sql.Contains("FROM translation_values", StringComparison.Ordinal));

        var project = (await db.Repo.GetProjectByCodeAsync("bulk-proj"))!;
        var bulk = project.Namespaces.Single(n => n.Code == "bulk");
        Assert.Equal(BulkCount + 1, bulk.Entries.Count);
        var added = bulk.Entries.Single(e => e.Key == "missing1");
        Assert.True(added.IsAutoReported);
        Assert.Equal("缺失 1", added.SourceText);
        Assert.Single(project.Namespaces.Single(n => n.Code == "other-ns").Entries);
        Assert.Single(project.Namespaces.Single(n => n.Code == "").Entries);

        // 已存在的 k00001 保持原样（不是自动上报、源文案不变）
        var existing = bulk.Entries.Single(e => e.Key == "k00001");
        Assert.False(existing.IsAutoReported);
        Assert.Equal("源文案 1", existing.SourceText);

        // 再报一次：全部已存在，不再创建
        Assert.Equal(0, await probe.Repo.RecordMissingKeysAsync(report));
        Assert.Equal(0, await probe.Repo.RecordMissingKeysAsync(new MissingKeyBatchReport("no-such-project", "zh-CN", report.Items)));
    }
}
