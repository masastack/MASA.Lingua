using System.Globalization;
using System.Text;
using System.Text.Json;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Serialization;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.SDK.AspNetCore;
using MASA.Lingua.SDK.Maui;
using MASA.Lingua.Server.Services;
using MASA.Lingua.Storage.EfCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// Key / 命名空间重复（键路径冲突）的端到端回归：
/// 发布产物的两种表示（嵌套 / 扁平）共用同一套路径规则；快照 / 磁盘 / 回滚恢复后扁平产物与 ETag 不变；
/// 写入口（缺词上报 / 手工新增 / 导入）对冲突有统一处理；命名空间 / Key 忽略大小写的纯函数与 SDK 取词口径
/// （存储层的大小写不敏感见 <see cref="CaseInsensitiveKeyTests"/>）。
/// 真实案例：MAUI 上项目根的裸 key「Confirm」与命名空间「Confirm」并存，客户端取不到翻译值。
/// </summary>
public sealed class KeyPathDuplicationTests
{
    // ------------------------------------------------------------------
    // 纯函数：路径规则、编译产物、快照编码
    // ------------------------------------------------------------------

    [Fact]
    public void Nested_Flat_And_Conflict_Detection_Share_One_Path_Rule()
    {
        // 命名空间 / key 带空白、根 key 含冒号：嵌套 / 扁平以前各自取不同规则，会出现「嵌套是 A、扁平是 B」的分叉
        (string Namespace, string Key, string Value)[] entries =
        [
            ("Confirm ", "Title", "标题"),
            ("", "a:b", "冒号"),
            ("common", " ok ", "好"),
        ];

        var nested = JsonBundleConverter.ToNestedJsonUtf8Bytes(entries);
        var flat = JsonBundleConverter.ToFlatJsonUtf8Bytes(entries);

        static string[] Paths(byte[] json) => JsonBundleConverter.ParseExistingJson(json)
            .Select(t => string.IsNullOrEmpty(t.Namespace) ? t.Key : $"{t.Namespace}.{t.Key}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Confirm.Title", "a.b", "common.ok"], Paths(flat));
        Assert.Equal(Paths(flat), Paths(nested));

        // 冲突检测与发布走同一条路径：根 key「a:b」与词条 a/b 是同一个位置
        var conflict = Assert.Single(JsonBundleConverter.DetectPathConflicts([("", "a:b"), ("a", "b")]));
        Assert.Equal("a.b", conflict.JsonPath);
        Assert.Empty(JsonBundleConverter.DetectPathConflicts([("Confirm ", "Title"), ("Confirm", "Other")]));
    }

    [Fact]
    public void ParseExistingJson_Should_Trim_Segments_And_Skip_Empty_Keys()
    {
        const string json = """{ " Confirm ": { " Title ": "t", "": "x" }, " a . b ": "v", "  ": "z" }""";

        var parsed = JsonBundleConverter.ParseExistingJson(Encoding.UTF8.GetBytes(json));

        Assert.Equal(
            [("Confirm", "Title", "t"), ("a", "b", "v")],
            parsed.OrderBy(p => p.Namespace, StringComparer.Ordinal).Select(p => (p.Namespace, p.Key, p.Value)));
    }

    private static CompiledBundle CompileConflictBundle(BundleCompiler compiler) =>
        compiler.Compile("p", "en-US",
        [
            new TranslationLayer("Project:en-US",
            [
                new TranslationItem("", "Confirm", "Confirm"),
                new TranslationItem("Confirm", "Title", "Confirm Title"),
                new TranslationItem("common", "ok", "OK"),
            ])
        ]);

    [Fact]
    public void FromPublishedBytes_Should_Restore_Both_Representations_And_The_Same_ETag()
    {
        var compiler = new BundleCompiler();
        var bundle = CompileConflictBundle(compiler);
        var nested = bundle.NestedJsonUtf8Bytes.ToArray();
        var flat = bundle.FlatJsonUtf8Bytes.ToArray();

        // 前提：嵌套被冲突挤掉了根 key，只有扁平文件完整
        Assert.DoesNotContain("\"Confirm\": \"Confirm\"", Encoding.UTF8.GetString(nested));
        Assert.Contains("\"Confirm\": \"Confirm\"", Encoding.UTF8.GetString(flat));

        // 只拿嵌套字节重建（旧行为）：扁平里的根 key 丢了，ETag 也变了 —— 这正是 .NET 客户端取不到 Confirm 的根因
        var lossy = compiler.FromPublishedBytes("p", "en-US", nested);
        Assert.DoesNotContain("\"Confirm\": \"Confirm\"", Encoding.UTF8.GetString(lossy.FlatJsonUtf8Bytes.Span));
        Assert.NotEqual(bundle.ETag, lossy.ETag);

        // 带上扁平字节还原：两种表示逐字节一致，ETag、条数都与发布时相同
        var exact = compiler.FromPublishedBytes("p", "en-US", nested, flat);
        Assert.Equal(bundle.ETag, exact.ETag);
        Assert.Equal(bundle.ContentHash, exact.ContentHash);
        Assert.Equal(nested, exact.NestedJsonUtf8Bytes.ToArray());
        Assert.Equal(flat, exact.FlatJsonUtf8Bytes.ToArray());
        Assert.Equal(bundle.EntryCount, exact.EntryCount);

        // 磁盘上的扁平文件损坏：抛 JsonException，调用方据此退回嵌套重建而不是把坏内容当线上版本
        Assert.ThrowsAny<JsonException>(
            () => compiler.FromPublishedBytes("p", "en-US", nested, Encoding.UTF8.GetBytes("{ not json")));
    }

    [Fact]
    public void EntryCount_Should_Count_Published_Entries_Not_Lookup_Aliases()
    {
        var bundle = CompileConflictBundle(new BundleCompiler());

        // 3 条词条（含被嵌套挤掉的根 key）；LookupEntries 为每条存了点 / 冒号 / 混合别名，条数不能拿来当词条数
        Assert.Equal(3, bundle.EntryCount);
        Assert.True(bundle.LookupEntries.Count > bundle.EntryCount);
    }

    [Fact]
    public void SnapshotContentCodec_V2_Should_RoundTrip_Both_Representations_And_Read_Legacy_Snapshots()
    {
        var bundle = CompileConflictBundle(new BundleCompiler());

        var packed = SnapshotContentCodec.Pack([("en-US", bundle.NestedJsonUtf8Bytes, bundle.FlatJsonUtf8Bytes)]);
        var unpacked = SnapshotContentCodec.UnpackBundles(packed)["en-US"];
        Assert.Equal(bundle.NestedJsonUtf8Bytes.ToArray(), unpacked.Nested);
        Assert.Equal(bundle.FlatJsonUtf8Bytes.ToArray(), unpacked.Flat);
        Assert.Equal(bundle.NestedJsonUtf8Bytes.ToArray(), SnapshotContentCodec.Unpack(packed)["en-US"]);

        // 旧版快照（只有嵌套）仍可读取，Flat 为 null 由调用方走校准
        var legacy = SnapshotContentCodec.Pack([("en-US", bundle.NestedJsonUtf8Bytes)]);
        var legacyUnpacked = SnapshotContentCodec.UnpackBundles(legacy)["en-US"];
        Assert.Equal(bundle.NestedJsonUtf8Bytes.ToArray(), legacyUnpacked.Nested);
        Assert.Null(legacyUnpacked.Flat);
        Assert.Equal(bundle.NestedJsonUtf8Bytes.ToArray(), SnapshotContentCodec.Unpack(legacy)["en-US"]);
    }

    [Fact]
    public void KeyPathIndex_Should_Detect_Conflicts_Incrementally()
    {
        var index = new KeyPathIndex([("Confirm", "Title"), ("common", "ok")]);

        Assert.NotNull(index.FindConflict("", "Confirm"));           // 根裸 key 与同名命名空间
        Assert.NotNull(index.FindConflict("", "common.ok"));          // 点分根 key 与「命名空间 + key」同一路径
        Assert.NotNull(index.FindConflict("Confirm.Title", "Deep")); // 在已有「值」下面再建子级
        Assert.Null(index.FindConflict("Confirm", "Confirm"));        // 命名空间下的同名 key 是不同层级，不冲突
        Assert.Null(index.FindConflict("Confirm", "Title"));          // 已存在的同一词条（更新）不算冲突
        Assert.Null(index.FindConflict("order", "submit"));

        index.Add("", "Solo");
        var conflict = index.FindConflict("Solo", "Child");
        Assert.NotNull(conflict);
        Assert.Equal("Solo", conflict!.JsonPath);
    }

    [Fact]
    public void KeyPathIndex_Should_Agree_With_Batch_Detection()
    {
        (string Namespace, string Key)[] pool =
        [
            ("", "A"), ("", "A.B"), ("A", "B"), ("A", "B.C"), ("", "a:b"),
            ("A.B", "C"), ("X", "Y"), ("", "X"), ("X ", "Y"),
        ];

        foreach (var existing in pool)
        {
            foreach (var candidate in pool)
            {
                if (existing == candidate)
                {
                    continue;
                }

                var incremental = new KeyPathIndex([existing]).FindConflict(candidate.Namespace, candidate.Key) is not null;
                var batch = JsonBundleConverter.DetectPathConflicts([existing, candidate]).Count > 0;
                Assert.True(
                    incremental == batch,
                    $"{existing} + {candidate}：增量判断 {incremental}，批量检测 {batch}");
            }
        }
    }

    // ------------------------------------------------------------------
    // 大小写不敏感：命名空间 / Key 忽略大小写唯一，全链路同一个口径
    // ------------------------------------------------------------------

    private static (string Path, string Value)[] PathValues(ReadOnlySpan<byte> json) => JsonBundleConverter.ParseExistingJson(json)
        .Select(t => (Path: string.IsNullOrEmpty(t.Namespace) ? t.Key : $"{t.Namespace}.{t.Key}", t.Value))
        .Order()
        .ToArray();

    [Fact]
    public void Bundle_Builders_Should_Treat_Case_Variants_As_One_Entry()
    {
        (string Namespace, string Key, string Value)[] entries =
        [
            ("Confirm", "Title", "first"),
            ("confirm", "title", "second"), // 同一个词条：后者覆盖前者，保留先出现的写法
            ("order", "x", "x"),
        ];

        var nested = JsonBundleConverter.ToNestedJsonUtf8Bytes(entries);
        var flat = JsonBundleConverter.ToFlatJsonUtf8Bytes(entries);

        Assert.Equal([("Confirm.Title", "second"), ("order.x", "x")], PathValues(flat));
        Assert.Equal(PathValues(flat), PathValues(nested));

        // 取词字典同样忽略大小写
        var lookup = JsonBundleConverter.FlattenToLookupDictionary(flat);
        Assert.Equal("second", lookup["CONFIRM.title"]);
        Assert.Equal("x", lookup["Order:X"]);
    }

    [Fact]
    public void Nested_Builder_Should_Merge_Case_Variant_Namespaces_Into_One_Object()
    {
        var nested = JsonBundleConverter.ToNestedJsonUtf8Bytes(
            [("Order", "submit", "提交"), ("order", "cancel", "取消"), ("ORDER", "Submit", "提交2")]);

        using var doc = JsonDocument.Parse(nested);
        var order = Assert.Single(doc.RootElement.EnumerateObject()); // 只有一个 Order 对象，不会并列出 Order / order / ORDER
        Assert.Equal("Order", order.Name);
        Assert.Equal(["cancel", "submit"], order.Value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("提交2", order.Value.GetProperty("submit").GetString());
    }

    [Fact]
    public void BundleCompiler_Should_Merge_Layers_By_Case_Insensitive_Identity()
    {
        var bundle = new BundleCompiler().Compile("p", "en-US",
        [
            new TranslationLayer("Shared", [new TranslationItem("Common", "OK", "shared ok"), new TranslationItem("Common", "Cancel", "shared cancel")]),
            new TranslationLayer("Project", [new TranslationItem("common", "ok", "project ok")]), // 项目层覆盖共享层的同一词条
        ]);

        Assert.Equal(2, bundle.EntryCount);
        Assert.Equal("project ok", bundle.LookupEntries["Common.OK"]);
        Assert.Equal("project ok", bundle.LookupEntries["common.ok"]);
        Assert.Equal("shared cancel", bundle.LookupEntries["COMMON.CANCEL"]);
    }

    [Fact]
    public void KeyPathIndex_Should_Treat_Case_Variants_As_The_Same_Path_And_The_Same_Entry()
    {
        var index = new KeyPathIndex([("Confirm", "Title"), ("common", "ok")]);

        Assert.Null(index.FindConflict("confirm", "title"));            // 写法不同的同一词条：是更新，不是冲突
        Assert.NotNull(index.FindConflict("", "CONFIRM"));              // 根裸 key 与命名空间 Confirm 撞车，不管大小写
        Assert.NotNull(index.FindConflict("", "confirm.TITLE"));        // 点分根 key 与「命名空间 + key」同一路径
        Assert.NotNull(index.FindConflict("CONFIRM.title", "Deep"));    // 在已有值下面再建子级
        Assert.Null(index.FindConflict("CONFIRM", "confirm"));          // 命名空间下的同名 key 是不同层级，不冲突

        // 与批量检测口径一致：写法不同、路径相同的两个不同词条才是「完全重复的路径」
        Assert.Empty(JsonBundleConverter.DetectPathConflicts([("Confirm", "Title"), ("confirm", "title")]));
        var duplicate = Assert.Single(JsonBundleConverter.DetectPathConflicts([("", "A.b"), ("a", "B")]));
        Assert.Equal(2, duplicate.ValueClaims.Count);
    }

    // ------------------------------------------------------------------
    // 数据库 + 发布器：重启 / 回滚 / 草稿差异 / 门禁提示
    // ------------------------------------------------------------------

    private sealed class TestEnv(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MASA.Lingua.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Harness : IDisposable
    {
        public required ILinguaRepository Repo { get; init; }
        public required string DatabaseName { get; init; }
        public required string Root { get; init; }

        public StaticBundlePublisher NewPublisher() => new(
            Repo, Repo, Repo,
            new BundleCompiler(),
            new LinguaSseBroadcaster(),
            new TestEnv(Root),
            new ConfigurationBuilder().Build(),
            NullLogger<StaticBundlePublisher>.Instance);

        public string AppData => Path.Combine(Root, "App_Data");

        public string NestedPath(string culture) => Path.Combine(AppData, "i18n", "shop-vue", $"{culture}.json");

        public string FlatPath(string culture) => Path.Combine(AppData, "i18n", "shop-vue", $"{culture}.flat.json");

        public void Dispose()
        {
            PostgresServer.Drop(DatabaseName);
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_keypath");
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();

        var root = Path.Combine(Path.GetTempPath(), $"lingua_keypath_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new Harness { Repo = provider.GetRequiredService<ILinguaRepository>(), DatabaseName = databaseName, Root = root };
    }

    private static Task UpsertAsync(ILinguaRepository repo, string ns, string key, string zh, string en) =>
        repo.UpsertEntryAsync("shop-vue", ns, key, zh, null, null,
            new Dictionary<string, string> { ["zh-CN"] = zh, ["en-US"] = en },
            status: TranslationStatus.Approved, acknowledgeIssues: true);

    /// <summary>pinbar / MAUI 的实际形状：项目根裸 key「Confirm」+ 命名空间「Confirm」（含同名 key 与 Title）。</summary>
    private static async Task SeedConfirmConflictAsync(ILinguaRepository repo)
    {
        await UpsertAsync(repo, "", "Confirm", "确认", "Confirm");
        await UpsertAsync(repo, "Confirm", "Confirm", "确认按钮", "Confirm Button");
        await UpsertAsync(repo, "Confirm", "Title", "确认标题", "Confirm Title");
    }

    private static readonly PublishRequest ForcePublish = new("v", Force: true, ForceReason: "键路径冲突知情发布");

    [Fact]
    public async Task Restart_And_Cold_Disk_Restore_Should_Keep_Both_Sides_Of_A_Path_Conflict()
    {
        using var h = await CreateHarnessAsync();
        var first = h.NewPublisher();
        await first.EnsurePublishedAsync("shop-vue");
        await SeedConfirmConflictAsync(h.Repo);
        Assert.NotNull(await first.PublishProjectAsync("shop-vue", ForcePublish));

        var live = (await first.GetOrCompileBundleAsync("shop-vue", "en-US"))!;
        var etag = live.ETag;
        var flat = live.FlatJsonUtf8Bytes.ToArray();
        Assert.Contains("\"Confirm\": \"Confirm\"", Encoding.UTF8.GetString(flat));
        Assert.True(File.Exists(h.FlatPath("en-US")), "发布应同时物化 {culture}.flat.json");

        // 1) 冷路径：内存缓存没有、直接从磁盘文件恢复（嵌套 + 扁平两个文件）
        var cold = (await h.NewPublisher().GetOrCompileBundleAsync("shop-vue", "en-US"))!;
        Assert.Equal(etag, cold.ETag);
        Assert.Equal(flat, cold.FlatJsonUtf8Bytes.ToArray());

        // 2) 重启恢复：磁盘文件全部丢失，由发布快照补回；线上版本逐字节不变，不新增发布记录
        Directory.Delete(h.AppData, recursive: true);
        var restarted = h.NewPublisher();
        Assert.False(await restarted.EnsurePublishedAsync("shop-vue"));

        var restored = (await restarted.GetOrCompileBundleAsync("shop-vue", "en-US"))!;
        Assert.Equal(etag, restored.ETag);
        Assert.Equal(flat, restored.FlatJsonUtf8Bytes.ToArray());
        Assert.Equal(flat, File.ReadAllBytes(h.FlatPath("en-US")));
        Assert.Equal(2, (await h.Repo.GetPublishSnapshotsAsync("shop-vue")).Count); // 首次引导发布 + v1，恢复不新增记录
    }

    [Fact]
    public async Task Rollback_Should_Restore_The_Flat_Side_Of_A_Path_Conflict_With_The_Same_ETag()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();
        await publisher.EnsurePublishedAsync("shop-vue");
        await SeedConfirmConflictAsync(h.Repo);
        var v1 = await publisher.PublishProjectAsync("shop-vue", ForcePublish);
        Assert.NotNull(v1);
        var v1Live = (await publisher.GetOrCompileBundleAsync("shop-vue", "zh-CN"))!;

        await UpsertAsync(h.Repo, "Confirm", "Title", "新的确认标题", "New Confirm Title");
        Assert.NotNull(await publisher.PublishProjectAsync("shop-vue", ForcePublish));
        Assert.NotEqual(v1Live.ETag, (await publisher.GetOrCompileBundleAsync("shop-vue", "zh-CN"))!.ETag);

        var rollback = await publisher.RollbackProjectAsync("shop-vue", v1!.Id);

        var afterRollback = (await publisher.GetOrCompileBundleAsync("shop-vue", "zh-CN"))!;
        Assert.Equal(v1.VersionHash, rollback.VersionHash);
        Assert.Equal(v1Live.ETag, afterRollback.ETag);
        Assert.Equal(v1Live.FlatJsonUtf8Bytes.ToArray(), afterRollback.FlatJsonUtf8Bytes.ToArray());
        Assert.Contains("\"Confirm\": \"确认\"", Encoding.UTF8.GetString(afterRollback.FlatJsonUtf8Bytes.Span));
        Assert.Equal(v1Live.FlatJsonUtf8Bytes.ToArray(), File.ReadAllBytes(h.FlatPath("zh-CN")));
    }

    /// <summary>旧版本写下的快照只有嵌套字节：草稿自那次发布以来没变过时，用草稿重新编译把扁平产物找回来。</summary>
    [Fact]
    public async Task Legacy_Nested_Only_Snapshot_Should_Be_Reconciled_From_An_Unchanged_Draft()
    {
        using var h = await CreateHarnessAsync();
        var first = h.NewPublisher();
        await first.EnsurePublishedAsync("shop-vue");
        await SeedConfirmConflictAsync(h.Repo);
        var v1 = (await first.PublishProjectAsync("shop-vue", ForcePublish))!;
        var etag = (await first.GetOrCompileBundleAsync("shop-vue", "en-US"))!.ETag;

        // 伪造旧格式快照：同一版本指纹，只保存嵌套 JSON（旧版本的快照就是这个样子）
        var project = (await h.Repo.GetProjectByCodeAsync("shop-vue"))!;
        var live = new List<CompiledBundle>();
        foreach (var culture in project.SupportedCultures)
        {
            live.Add((await first.GetOrCompileBundleAsync("shop-vue", culture))!);
        }

        await h.Repo.RecordPublishSnapshotAsync(new PublishSnapshot
        {
            ProjectId = project.Id,
            VersionHash = v1.VersionHash,
            PublishedAt = DateTimeOffset.UtcNow.AddSeconds(1),
            PublishedCultures = live.Select(b => b.Culture).ToList(),
            Content = SnapshotContentCodec.Pack(live.Select(b => (b.Culture, b.NestedJsonUtf8Bytes)))
        });

        Directory.Delete(h.AppData, recursive: true);
        var restarted = h.NewPublisher();
        Assert.False(await restarted.EnsurePublishedAsync("shop-vue"));

        var restored = (await restarted.GetOrCompileBundleAsync("shop-vue", "en-US"))!;
        Assert.Equal(etag, restored.ETag);
        Assert.Contains("\"Confirm\": \"Confirm\"", Encoding.UTF8.GetString(restored.FlatJsonUtf8Bytes.Span));
        Assert.True(File.Exists(h.FlatPath("en-US")));
    }

    [Fact]
    public async Task Legacy_Snapshot_With_A_Changed_Draft_Should_Still_Restore_Without_Failing_Startup()
    {
        using var h = await CreateHarnessAsync();
        var first = h.NewPublisher();
        await first.EnsurePublishedAsync("shop-vue");
        await SeedConfirmConflictAsync(h.Repo);
        var v1 = (await first.PublishProjectAsync("shop-vue", ForcePublish))!;

        var project = (await h.Repo.GetProjectByCodeAsync("shop-vue"))!;
        var live = new List<CompiledBundle>();
        foreach (var culture in project.SupportedCultures)
        {
            live.Add((await first.GetOrCompileBundleAsync("shop-vue", culture))!);
        }

        await h.Repo.RecordPublishSnapshotAsync(new PublishSnapshot
        {
            ProjectId = project.Id,
            VersionHash = v1.VersionHash,
            PublishedAt = DateTimeOffset.UtcNow.AddSeconds(1),
            PublishedCultures = live.Select(b => b.Culture).ToList(),
            Content = SnapshotContentCodec.Pack(live.Select(b => (b.Culture, b.NestedJsonUtf8Bytes)))
        });

        // 草稿之后又改了：无法用草稿还原那个版本，保留嵌套重建结果继续启动（日志警告），不能让启动失败
        await UpsertAsync(h.Repo, "order", "laterKey", "后来的词条", "Later key");
        var restarted = h.NewPublisher();
        Assert.False(await restarted.EnsurePublishedAsync("shop-vue"));
        Assert.NotNull(await restarted.GetOrCompileBundleAsync("shop-vue", "en-US"));
    }

    [Fact]
    public async Task Draft_Diff_Should_See_A_Change_That_Only_Exists_On_The_Flat_Side()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();
        await publisher.EnsurePublishedAsync("shop-vue");
        await SeedConfirmConflictAsync(h.Repo);
        await publisher.PublishProjectAsync("shop-vue", ForcePublish);
        Assert.False((await publisher.GetDraftDiffAsync("shop-vue")).HasChanges);

        // 只改被嵌套 JSON 挤掉的根 key 译文：嵌套字节不变，扁平字节变化 —— 草稿差异必须能看到
        await h.Repo.UpsertEntryAsync("shop-vue", "", "Confirm", "确认", null, null,
            new Dictionary<string, string> { ["en-US"] = "Confirm (new)" },
            status: TranslationStatus.Approved, acknowledgeIssues: true);

        var diff = await publisher.GetDraftDiffAsync("shop-vue");
        Assert.True(diff.HasChanges);
        Assert.Equal(1, diff.Changed);
    }

    [Fact]
    public async Task Manifest_Entry_Count_Should_Include_Entries_Dropped_By_The_Nested_Representation()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();
        await publisher.EnsurePublishedAsync("shop-vue");
        await SeedConfirmConflictAsync(h.Repo);
        var snapshot = await publisher.PublishProjectAsync("shop-vue", ForcePublish);

        var bundle = (await publisher.GetOrCompileBundleAsync("shop-vue", "en-US"))!;
        var flatCount = JsonBundleConverter.ParseExistingJson(bundle.FlatJsonUtf8Bytes.Span).Count;
        var nestedCount = JsonBundleConverter.ParseExistingJson(bundle.NestedJsonUtf8Bytes.Span).Count;
        Assert.True(flatCount > nestedCount, "冲突项目里扁平比嵌套多一条被挤掉的根词条");

        var manifest = (await publisher.GetManifestAsync("shop-vue"))!;
        Assert.Equal(flatCount, Assert.Single(manifest.Cultures, c => c.Culture == "en-US").EntryCount);
        Assert.Equal(manifest.Cultures.Max(c => c.EntryCount), snapshot!.EntryCount);
    }

    [Fact]
    public async Task PublishCheck_Should_Warn_About_Allowed_Conflicts()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();
        await publisher.EnsurePublishedAsync("shop-vue");
        await SeedConfirmConflictAsync(h.Repo);

        // 默认阻断冲突：阻断理由里有，警告里不重复
        var blocked = await publisher.CheckProjectAsync("shop-vue");
        Assert.Contains(blocked.Blockers, b => b.Code == PublishChecker.PathConflictCode);
        Assert.DoesNotContain(blocked.Warnings, w => w.Contains("键路径冲突"));

        // 策略放行后：不再阻断，但冲突在警告里留痕，不会「隐形」
        await h.Repo.SavePublishPolicyAsync("shop-vue", new PublishPolicy { BlockOnKeyPathConflicts = false });
        var allowed = await publisher.CheckProjectAsync("shop-vue");
        Assert.DoesNotContain(allowed.Blockers, b => b.Code == PublishChecker.PathConflictCode);
        Assert.Contains(allowed.Warnings, w => w.Contains("键路径冲突") && w.Contains("已按发布策略放行"));
    }

    // ------------------------------------------------------------------
    // 写入口：缺词上报 / 新增预检 / 导入
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordMissingKeys_Should_Skip_Conflicting_Reports_And_Trim_Names()
    {
        using var h = await CreateHarnessAsync();
        await UpsertAsync(h.Repo, "Confirm", "Title", "确认标题", "Confirm Title");

        var created = await h.Repo.RecordMissingKeysAsync(new MissingKeyBatchReport("shop-vue", "en-US",
        [
            new MissingKeyReportItem("", "Confirm", "确认"),          // 与命名空间 Confirm 冲突：不创建
            new MissingKeyReportItem("order ", " newKey ", "新词条"), // 首尾空白被去掉后落到 order/newKey
            new MissingKeyReportItem("", "plain", "普通"),
            new MissingKeyReportItem("", "plain.sub", "子级"),         // 与同批里刚创建的根词条 plain 冲突：不创建
            new MissingKeyReportItem("Confirm", "Confirm", "同名 key"), // 命名空间下的同名 key 不冲突
            new MissingKeyReportItem("order", "   ", "空 key"),        // 空 key 没有可发布的路径：忽略
        ]));

        Assert.Equal(3, created);
        var project = (await h.Repo.GetProjectByCodeAsync("shop-vue"))!;
        var all = project.Namespaces
            .SelectMany(n => n.Entries.Select(e => (Namespace: n.Code, e.Key)))
            .ToHashSet();
        Assert.Contains(("order", "newKey"), all);
        Assert.Contains(("", "plain"), all);
        Assert.Contains(("Confirm", "Confirm"), all);
        Assert.DoesNotContain(("", "Confirm"), all);
        Assert.DoesNotContain(("", "plain.sub"), all);
        Assert.DoesNotContain(all, e => e.Key != e.Key.Trim() || e.Namespace != e.Namespace.Trim());

        // 重复上报幂等：已存在的不再创建
        Assert.Equal(0, await h.Repo.RecordMissingKeysAsync(new MissingKeyBatchReport("shop-vue", "en-US",
            [new MissingKeyReportItem("order", "newKey", "新词条")])));
    }

    [Fact]
    public async Task FindKeyPathConflicts_Should_Cover_Own_Inherited_And_Shared_Namespaces()
    {
        using var h = await CreateHarnessAsync();
        await UpsertAsync(h.Repo, "Confirm", "Title", "确认标题", "Confirm Title");

        // 项目自有命名空间
        var own = await h.Repo.FindKeyPathConflictsAsync("shop-vue", "", "Confirm");
        Assert.Contains("键路径冲突", Assert.Single(own));
        Assert.Empty(await h.Repo.FindKeyPathConflictsAsync("shop-vue", "Confirm", "Title"));   // 已存在 = 更新
        Assert.Empty(await h.Repo.FindKeyPathConflictsAsync("shop-vue", "Confirm", "Confirm")); // 同名 key 不冲突
        Assert.Empty(await h.Repo.FindKeyPathConflictsAsync("shop-vue", "order", "brandNew"));

        // 项目继承的共享命名空间（种子里 shop-vue 继承 common）：根裸 key「common」会挤掉整个共享命名空间
        Assert.NotEmpty(await h.Repo.FindKeyPathConflictsAsync("shop-vue", "", "common"));

        // 共享池本身：与项目无关，只看共享命名空间
        Assert.NotEmpty(await h.Repo.FindKeyPathConflictsAsync("__shared__", "", "errors"));
        Assert.Empty(await h.Repo.FindKeyPathConflictsAsync("__shared__", "", "Confirm")); // 项目里的 Confirm 命名空间不在共享池里
        Assert.Empty(await h.Repo.FindKeyPathConflictsAsync("no-such-project", "", "Confirm"));
    }

    [Fact]
    public async Task FindKeyPathConflicts_Should_Compare_Paths_Not_Strings_For_Dotted_Namespaces_And_Keys()
    {
        using var h = await CreateHarnessAsync();
        await UpsertAsync(h.Repo, "Confirm.Dialog", "Title", "确认对话框标题", "Dialog title"); // 路径 Confirm/Dialog/Title
        await UpsertAsync(h.Repo, "A", "B.C", "甲乙丙", "A B C");                              // 路径 A/B/C（key 自带点）
        await UpsertAsync(h.Repo, "", "R.X", "根上的点号 key", "Root dotted key");               // 路径 R/X

        async Task<bool> ConflictsAsync(string ns, string key) =>
            (await h.Repo.FindKeyPathConflictsAsync("shop-vue", ns, key)).Count > 0;

        // 候选值压在整个命名空间之上（命名空间路径更长或相同）
        Assert.True(await ConflictsAsync("", "Confirm"));
        Assert.True(await ConflictsAsync("", "Confirm.Dialog"));
        Assert.True(await ConflictsAsync("Confirm", "Dialog"));
        Assert.True(await ConflictsAsync("", "R"));

        // 同一路径的另一种写法（命名空间 / key 的切分不同）
        Assert.True(await ConflictsAsync("Confirm", "Dialog.Title"));
        Assert.True(await ConflictsAsync("A.B", "C"));
        Assert.True(await ConflictsAsync("", "A.B.C"));
        Assert.True(await ConflictsAsync("R", "X"));

        // 压在已有值之下
        Assert.True(await ConflictsAsync("A", "B"));
        Assert.True(await ConflictsAsync("A.B.C", "D"));

        // 不冲突：已存在（更新）、兄弟路径、完全无关
        Assert.False(await ConflictsAsync("Confirm.Dialog", "Title"));
        Assert.False(await ConflictsAsync("Confirm", "Other"));
        Assert.False(await ConflictsAsync("Confirm.Dialog", "Body"));
        Assert.False(await ConflictsAsync("R", "Y"));
        Assert.False(await ConflictsAsync("A", "B2"));
        Assert.False(await ConflictsAsync("Zed", "Q"));
    }

    [Fact]
    public async Task Import_Should_Report_Only_The_Path_Conflicts_It_Introduced()
    {
        using var h = await CreateHarnessAsync();

        // 旧项目常见的扁平导出：根 key「Confirm」与「Confirm.Title」并存
        var conflicting = await h.Repo.ImportJsonBundleAsync("shop-vue", "zh-CN",
            Encoding.UTF8.GetBytes("""{ "Confirm": "确认", "Confirm.Title": "标题", "ok": "好" }"""));
        Assert.Equal(1, conflicting.PathConflictCount);
        Assert.Contains("Confirm", Assert.Single(conflicting.PathConflicts));
        Assert.Equal(3, conflicting.TotalParsed); // 导入照常入库，冲突只是提示

        // 之后再导入与冲突无关的内容：存量冲突不在这次结果里重复出现
        var unrelated = await h.Repo.ImportJsonBundleAsync("shop-vue", "zh-CN",
            Encoding.UTF8.GetBytes("""{ "brandNew": { "a": "1" } }"""));
        Assert.Equal(0, unrelated.PathConflictCount);
        Assert.Empty(unrelated.PathConflicts);
    }

    // ------------------------------------------------------------------
    // SDK：取词忽略大小写与 MAUI 端到端
    // ------------------------------------------------------------------

    private sealed class OrderController;

    [Fact]
    public void Localizers_And_Store_Should_Resolve_Names_Case_Insensitively()
    {
        var store = new LinguaFrozenMemoryStore();
        store.UpdateCultureFromJsonBytes("en-US", """{ "Order": { "submit": "Submit", "Cancel": "Cancel" }, "Confirm": { "Confirm": "Confirm button" } }"""u8);

        // 存储层：命名空间 / Key 写法随意
        Assert.True(store.TryGetValue("en-US", "order.SUBMIT", out var submit));
        Assert.Equal("Submit", submit);
        Assert.True(store.TryGetValue("en-US", "CONFIRM:confirm", out var confirm));
        Assert.Equal("Confirm button", confirm);

        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("en-US");
        try
        {
            // 按类型解析：前缀是类型名小写 order.，库里命名空间是 Order，照样取得到
            IStringLocalizer<OrderController> localizer = new LinguaStringLocalizer<OrderController>(store);

            Assert.Equal("Submit", localizer["submit"].Value);
            Assert.Equal("Cancel", localizer["cancel"].Value);
            Assert.True(localizer["nothing"].ResourceNotFound);
            Assert.Equal("order.nothing", localizer["nothing"].Value);
            Assert.Equal(
                ["Cancel", "submit"],
                localizer.GetAllStrings(false).Select(s => s.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task Maui_Client_Should_Resolve_Root_Key_Namespace_And_Same_Named_Key_Together()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<ILinguaRepository>();
        var publisher = factory.Services.GetRequiredService<StaticBundlePublisher>();

        // 用户在 MAUI 上遇到的数据形状：Key 与命名空间都叫 Confirm（还叠加了根裸 key Confirm）
        await SeedConfirmConflictAsync(repo);
        Assert.NotNull(await publisher.PublishProjectAsync("shop-vue", ForcePublish));

        var cache = Path.Combine(Path.GetTempPath(), $"lingua_keypath_maui_{Guid.NewGuid():N}");
        try
        {
            using var localizer = new LinguaLocalizer(new LinguaClientOptions
            {
                ProjectCode = "shop-vue",
                ServerEndpoint = http.BaseAddress!.ToString(),
                LocalJsonDirectory = cache
            }, http);
            await localizer.InitializeAsync();
            localizer.SetCulture("zh-CN");

            Assert.Equal("确认", localizer["Confirm"]);              // 项目根的裸 key（嵌套 JSON 里被命名空间挤掉的一侧）
            Assert.Equal("确认按钮", localizer["Confirm.Confirm"]);   // 命名空间 Confirm 下的 key Confirm
            Assert.Equal("确认标题", localizer["Confirm.Title"]);

            // 命名空间 / Key 忽略大小写：业务代码写法不必与后台录入的大小写一致
            Assert.Equal("确认", localizer["CONFIRM"]);
            Assert.Equal("确认按钮", localizer["confirm.CONFIRM"]);
            Assert.Equal("确认标题", localizer["confirm:title"]);
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch (IOException) { }
        }
    }
}
