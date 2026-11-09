using System.Text;
using System.Text.Json;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.Core.Serialization;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 导入路径的规模正确性与 key 深度防护：
/// 1. 大 JSON（数千条 key）导入的结果计数与幂等性 —— 按需加载的新实现必须与原「整树加载」逐项一致；
/// 2. 超深 key 在数据入口（导入解析 / Upsert / 缺词上报）被拒绝 —— 发布期构建嵌套 JSON 的递归遇到
///    几千段的 key 会栈溢出，栈溢出无法捕获，是进程级死亡（无异常、无日志）。
/// </summary>
public sealed class ImportScaleAndGuardTests
{
    // ------------------------------------------------------------------
    // key 深度防护（纯内存，不起库）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(4, false)]   // 正常 i18n 结构：放行
    [InlineData(20, false)]  // 恰好在上限内：放行
    [InlineData(21, true)]   // 超限：拒绝
    public void ParseExistingJson_Guards_Flat_Key_Depth(int segments, bool shouldThrow)
    {
        var key = string.Join('.', Enumerable.Range(0, segments).Select(i => $"s{i}"));
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "文本" }));

        if (shouldThrow)
        {
            var ex = Assert.Throws<LinguaValidationException>(() => JsonBundleConverter.ParseExistingJson(json));
            Assert.Contains("层级过深", ex.Message);
        }
        else
        {
            var parsed = JsonBundleConverter.ParseExistingJson(json);
            Assert.Single(parsed);
        }
    }

    [Fact]
    public void ParseExistingJson_Guards_Nested_Json_Depth()
    {
        // 嵌套 25 层的对象树（顶层属性展开后同样受上限约束）
        const int depth = 25;
        var sb = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            sb.Append($"\"l{i}\":{{");
        }

        sb.Append("\"leaf\":\"文本\"");
        sb.Append(new string('}', depth));
        var json = Encoding.UTF8.GetBytes('{' + sb.ToString() + '}');

        Assert.Throws<LinguaValidationException>(() => JsonBundleConverter.ParseExistingJson(json));
    }

    [Fact]
    public void EnsureKeySegments_Combines_Namespace_And_Key_Segments()
    {
        var ns10 = string.Join('.', Enumerable.Range(0, 10).Select(i => $"n{i}"));
        var key10 = string.Join('.', Enumerable.Range(0, 10).Select(i => $"k{i}"));
        var key11 = string.Join('.', Enumerable.Range(0, 11).Select(i => $"k{i}"));

        // 命名空间段与 key 段合计计算：10 + 10 = 20 放行，10 + 11 = 21 拒绝
        JsonBundleConverter.EnsureKeySegments(ns10, key10);
        Assert.Throws<LinguaValidationException>(() => JsonBundleConverter.EnsureKeySegments(ns10, key11));

        // 空命名空间（项目根）只有 key 自身的段数
        JsonBundleConverter.EnsureKeySegments("", key11);
        JsonBundleConverter.EnsureKeySegments("", "order:submit".Replace(':', '.'));
    }

    // ------------------------------------------------------------------
    // 大 JSON 导入：计数与幂等（走真库，覆盖按需加载的新实现）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Import_Large_Json_Counts_Accurate_And_Reimport_Is_Idempotent()
    {
        const int count = 2000;
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_import_scale");
        var repo = db.Repo;
        await repo.CreateProjectAsync("import-scale");

        var bundle = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new Dictionary<string, Dictionary<string, string>>
            {
                ["bulk"] = Enumerable.Range(0, count).ToDictionary(i => $"k{i:D5}", i => $"源文案 {i}")
            }));

        // 首次导入：全部新增（词条 + 译文）
        var first = await repo.ImportJsonBundleAsync("import-scale", "zh-CN", bundle, "bulk");
        Assert.Equal(count, first.TotalParsed);
        Assert.Equal(count, first.AddedEntries);
        Assert.Equal(count, first.UpdatedTranslations);
        Assert.Equal(["bulk"], first.AffectedNamespaces);

        // 重复导入同一份：不新增、不改文本（幂等），计数与老实现一致
        var second = await repo.ImportJsonBundleAsync("import-scale", "zh-CN", bundle, "bulk");
        Assert.Equal(count, second.TotalParsed);
        Assert.Equal(0, second.AddedEntries);
        Assert.Equal(0, second.UpdatedTranslations);

        // 落库结果抽查：词条数、源文案、语种归属全部正确
        var project = await repo.GetProjectByCodeAsync("import-scale");
        var ns = Assert.Single(project!.Namespaces);
        Assert.Equal("bulk", ns.Code);
        Assert.Equal(count, ns.Entries.Count);
        var sample = ns.Entries.Single(e => e.Key == "k01000");
        Assert.Equal("源文案 1000", sample.SourceText);
        Assert.Equal("源文案 1000", sample.Translations.Single(t => t.Culture == "zh-CN").Text);
    }

    [Fact]
    public async Task Import_Rejects_Too_Deep_Key_At_Entry()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_import_depth");
        var repo = db.Repo;
        await repo.CreateProjectAsync("import-depth");

        var key = string.Join('.', Enumerable.Range(0, 21).Select(i => $"s{i}"));
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "文本" }));

        await Assert.ThrowsAsync<LinguaValidationException>(
            () => repo.ImportJsonBundleAsync("import-depth", "zh-CN", json));

        // Upsert 与缺词上报同样在入口拒绝
        await Assert.ThrowsAsync<LinguaValidationException>(
            () => repo.UpsertEntryAsync("import-depth", "", key, "源文案", context: null, maxLength: null));
        await Assert.ThrowsAsync<LinguaValidationException>(
            () => repo.RecordMissingKeysAsync(new MissingKeyBatchReport("import-depth", "zh-CN",
                [new MissingKeyReportItem("", key, "默认文案")])));
    }
}
