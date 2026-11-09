using System.Diagnostics;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Validation;
using MASA.Lingua.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MASA.Lingua.Tests;

/// <summary>
/// 大数据量下「一次操作的代价」基准：5k 词条随常规测试一起跑；20k 词条较慢，设环境变量
/// <c>LINGUA_BENCH=1</c> 才运行（<c>dotnet test --filter ScaleBenchmark</c>，加 <c>-v n</c> 看耗时表）。
/// <para>
/// 对照口径：新路径 vs 「改造前每次操作都要付的全量代价」的等价物（同一份代码里仍可直接调用的全量接口）——
/// 单格保存 = 单词条写入 vs 全量加载项目词条树；单格校验 = 增量校验一个词条 vs 全量校验；
/// 概览 = 投影查询与缓存命中 vs 全量项目；批量写入 = 一个事务 vs 逐条事务。
/// 耗时断言只比较同一台机器上的比值，且留了很大余量（真实比值通常是断言门槛的数倍到数百倍），不依赖绝对时间。
/// </para>
/// </summary>
public sealed class ScaleBenchmarkTests(ITestOutputHelper output)
{
    private const string Project = "bench-proj";
    private const string Namespace = "bulk";

    [Fact]
    public Task Scale_5k_Entries() => RunAsync(5_000);

    [BenchFact]
    public Task Scale_20k_Entries() => RunAsync(20_000);

    private sealed class BenchEnv(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MASA.Lingua.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<double> MedianMsAsync(Func<Task> action, int runs)
    {
        var samples = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return samples[runs / 2];
    }

    private static double MedianMs(Action action, int runs)
    {
        var samples = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return samples[runs / 2];
    }

    private async Task RunAsync(int count)
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_bench");
        var repo = db.Repo;
        var root = Path.Combine(Path.GetTempPath(), $"lingua_bench_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var rows = new List<(string Scenario, string Old, double OldMs, string New, double NewMs)>();
            void Row(string scenario, string oldLabel, double oldMs, string newLabel, double newMs) =>
                rows.Add((scenario, oldLabel, oldMs, newLabel, newMs));

            // ---- 播种（走正式导入路径）----
            var seedWatch = Stopwatch.StartNew();
            await EntryWriteLoadingTests.SeedBulkAsync(repo, Project, Namespace, count);
            var seedMs = seedWatch.Elapsed.TotalMilliseconds;

            // 预热：EF 模型 / JIT 只付一次，不计入对比
            await repo.GetProjectByCodeAsync(Project);
            await repo.UpsertEntryAsync(Project, Namespace, "k00000", "源文案 0", null, null,
                new Dictionary<string, string> { ["en-US"] = "Text 0" });

            // ---- 1. 单格保存：单词条写入 vs 全量加载项目词条树 ----
            var fullLoadMs = await MedianMsAsync(() => repo.GetProjectByCodeAsync(Project), runs: 3);
            var key = 0;
            var singleUpsertMs = await MedianMsAsync(
                () => repo.UpsertEntryAsync(Project, Namespace, $"k{(key += 7) % count:D5}", $"源文案 {key % count}", null, null,
                    new Dictionary<string, string> { ["en-US"] = $"Edited {key}" }, status: TranslationStatus.Approved),
                runs: 7);
            Row("单格保存（写库）", "全量加载词条树", fullLoadMs, "单词条写入", singleUpsertMs);

            // ---- 2. 校验：全量 vs 增量一个词条 ----
            var project = (await repo.GetProjectByCodeAsync(Project))!;
            var entries = project.Namespaces.SelectMany(n => n.Entries).ToList();
            Assert.Equal(count, entries.Count);
            var glossary = (await repo.GetGlossaryTermsAsync()).ToList();
            var validator = new DeterministicTranslationValidator();
            string[] targets = ["en-US", "ja-JP"];

            var fullValidateMs = MedianMs(() =>
            {
                foreach (var entry in entries)
                {
                    foreach (var culture in targets)
                    {
                        var text = entry.Translations.FirstOrDefault(t => t.Culture == culture)?.Text;
                        validator.ValidateEntry(entry, culture, text, glossary);
                    }
                }
            }, runs: 3);

            var target = entries[count / 2];
            var incrementalMs = MedianMs(() =>
            {
                var matched = GlossaryMatcher.FindMatchedTerms(target.SourceText, glossary);
                foreach (var culture in targets)
                {
                    var text = target.Translations.FirstOrDefault(t => t.Culture == culture)?.Text;
                    validator.ValidateEntryWithMatches(target, culture, text, matched);
                }
            }, runs: 101);
            Row("单格保存（校验）", "全量校验所有格子", fullValidateMs, "增量校验一个词条", incrementalMs);

            // ---- 3. 项目概览 / 全量项目：冷读 vs 缓存命中 ----
            async Task<double> ColdThenHitAsync(Func<Task> read, string scenario, string oldLabel, double oldMs)
            {
                await repo.SavePublishPolicyAsync(Project, new PublishPolicy()); // 写入 → 缓存失效，下一次读是冷读
                var cold = await MedianMsAsync(read, runs: 1);
                var hit = await MedianMsAsync(read, runs: 5);
                Row(scenario, oldLabel, oldMs > 0 ? oldMs : cold, "缓存命中", hit);
                return cold;
            }

            var allProjectsCold = await ColdThenHitAsync(() => repo.GetAllProjectsAsync(), "全量项目读取", "冷读（查库）", 0);
            var overviewCold = await ColdThenHitAsync(() => repo.GetProjectOverviewsAsync(), "项目概览读取", "冷读（查库）", 0);
            await ColdThenHitAsync(() => repo.GetProjectImpactSummariesAsync(), "影响面读取", "冷读（查库）", 0);
            Row("项目概览 vs 全量项目（冷读）", "全量项目", allProjectsCold, "概览投影", overviewCold);

            // ---- 4. 批量写入：一个事务 vs 逐条事务（AI 批量补全一批 50 条）----
            const int batch = 50;
            var batchItems = Enumerable.Range(0, batch)
                .Select(i => new TranslationUpsertBatchItem(
                    Namespace, $"k{(i * 13) % count:D5}", $"源文案 {(i * 13) % count}", null, null,
                    new Dictionary<string, string> { ["ja-JP"] = $"日本語 {i}" }, TranslationStatus.AiTranslated))
                .ToList();
            var sequentialMs = await MedianMsAsync(async () =>
            {
                foreach (var item in batchItems)
                {
                    await repo.UpsertEntryAsync(Project, item.NamespaceCode, item.Key, item.SourceText, null, null,
                        new Dictionary<string, string>(item.Translations), status: item.Status);
                }
            }, runs: 1);
            var batchMs = await MedianMsAsync(() => repo.UpsertTranslationsBatchAsync(Project, batchItems), runs: 3);
            Row($"批量写入 {batch} 条译文", "逐条事务", sequentialMs, "一个事务", batchMs);

            // ---- 5. 发布 / 草稿差异：绝对耗时（改造后一次读库 + 逐语种并行编译）----
            var sse = new LinguaSseBroadcaster();
            var publisher = new StaticBundlePublisher(
                repo, repo, repo, new BundleCompiler(), sse, new BenchEnv(root),
                new ConfigurationBuilder().Build(), NullLogger<StaticBundlePublisher>.Instance);
            await publisher.EnsurePublishedAsync(Project); // 先固定一个线上版本，草稿差异才有对比基线
            await repo.UpsertEntryAsync(Project, Namespace, "k00001", "源文案 1", null, null,
                new Dictionary<string, string> { ["en-US"] = "Changed after publish" }, status: TranslationStatus.Approved);
            var overviewMs = await MedianMsAsync(() => publisher.GetReleaseOverviewAsync(Project), runs: 3);
            var checkOnlyMs = MedianMs(() => PublishChecker.Check(project, project.Namespaces, glossary), runs: 3);
            var publishMs = await MedianMsAsync(
                () => publisher.PublishProjectAsync(Project, new PublishRequest("bench", Force: true, ForceReason: "bench")),
                runs: 1);

            PrintTable(count, seedMs, rows, overviewMs, checkOnlyMs, publishMs);

            // 断言：同机比值，留足余量（真实比值见输出表）
            Assert.True(singleUpsertMs < fullLoadMs / 3,
                $"单词条写入 ({singleUpsertMs:F1} ms) 应远快于全量加载词条树 ({fullLoadMs:F1} ms)");
            Assert.True(incrementalMs < fullValidateMs / 100,
                $"增量校验 ({incrementalMs:F3} ms) 应比全量校验 ({fullValidateMs:F1} ms) 快两个数量级以上");
            Assert.True(overviewCold < allProjectsCold / 2,
                $"概览投影冷读 ({overviewCold:F1} ms) 不应随词条数增长，应明显快于全量项目 ({allProjectsCold:F1} ms)");
            Assert.True(batchMs < sequentialMs / 2,
                $"批量写入 ({batchMs:F1} ms) 应明显快于逐条事务 ({sequentialMs:F1} ms)");
            Assert.All(rows.Where(r => r.New == "缓存命中"), r =>
                Assert.True(r.NewMs < r.OldMs / 3, $"{r.Scenario}：缓存命中 ({r.NewMs:F2} ms) 应远快于冷读 ({r.OldMs:F1} ms)"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private void PrintTable(
        int count,
        double seedMs,
        List<(string Scenario, string Old, double OldMs, string New, double NewMs)> rows,
        double overviewMs,
        double checkOnlyMs,
        double publishMs)
    {
        output.WriteLine($"=== 规模基准：{count:N0} 个词条 × 2 个语种（播种耗时 {seedMs:F0} ms）===");
        output.WriteLine($"{"场景",-26}{"改造前等价物",-18}{"耗时(ms)",12}  {"新路径",-14}{"耗时(ms)",12}  {"倍数",8}");
        foreach (var (scenario, oldLabel, oldMs, newLabel, newMs) in rows)
        {
            var ratio = newMs > 0 ? oldMs / newMs : double.PositiveInfinity;
            output.WriteLine($"{scenario,-24}{oldLabel,-16}{oldMs,12:F2}  {newLabel,-12}{newMs,12:F3}  {ratio,7:F1}x");
        }

        output.WriteLine($"发布页概览（检查 + 草稿差异，一次读库 + 逐语种并行）  {overviewMs:F1} ms（其中纯门禁检查 {checkOnlyMs:F1} ms）");
        output.WriteLine($"强制发布（读库 + 编译 + 落盘 + 快照）                  {publishMs:F1} ms");
    }
}

/// <summary>仅在设置了 <c>LINGUA_BENCH=1</c> 时运行的慢速基准。</summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LINGUA_BENCH") != "1")
        {
            Skip = "20k 词条基准较慢：设置环境变量 LINGUA_BENCH=1 后运行。";
        }
    }
}
