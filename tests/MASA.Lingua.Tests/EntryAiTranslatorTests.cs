using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Validation;
using MASA.Lingua.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// <see cref="EntryAiTranslator"/>（从词条矩阵页面抽出的 AI 编排）：按语种并发、失败不牵连其他语种、
/// 写回按作用域分批、内存词条树对不上时要求重载。用假的 AI 服务与假的词条存储，单独测编排而不碰数据库。
/// </summary>
public sealed class EntryAiTranslatorTests
{
    /// <summary>按语种返回译文；<c>throwOn</c> 里列出的语种在返回部分结果之后抛失败。</summary>
    private sealed class FakeAi : IAiTranslationService
    {
        private readonly HashSet<string> _throwOn;
        private readonly HashSet<string> _partialBeforeThrow;

        public FakeAi(IEnumerable<string>? throwOn = null, IEnumerable<string>? partialBeforeThrow = null)
        {
            _throwOn = throwOn?.ToHashSet() ?? [];
            _partialBeforeThrow = partialBeforeThrow?.ToHashSet() ?? [];
        }

        public List<string> RequestedCultures { get; } = [];
        public List<AiTranslationRequestItem> RequestedItems { get; } = [];
        public int MaxConcurrentObserved { get; private set; }

        private int _inFlight;

        public async Task<IReadOnlyList<AiTranslationResultItem>> TranslateBatchAsync(
            string projectCode,
            string sourceCulture,
            IReadOnlyList<string> targetCultures,
            IReadOnlyList<AiTranslationRequestItem> items,
            IReadOnlyList<GlossaryTerm> glossaryTerms,
            CancellationToken ct = default)
        {
            var culture = Assert.Single(targetCultures);
            lock (RequestedCultures)
            {
                RequestedCultures.Add(culture);
                RequestedItems.AddRange(items);
            }

            var now = Interlocked.Increment(ref _inFlight);
            MaxConcurrentObserved = Math.Max(MaxConcurrentObserved, now);
            try
            {
                await Task.Delay(20, ct); // 让多个语种真的重叠，才能观察到并发

                var translated = items
                    .Select(i => new AiTranslationResultItem(i.EntryId, culture, $"{culture}:{i.Key}", []))
                    .ToList();

                if (_throwOn.Contains(culture))
                {
                    throw new AiTranslationException(
                        $"{culture} 失败",
                        null,
                        _partialBeforeThrow.Contains(culture) ? translated : []);
                }

                return translated;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>记录写回请求的假词条存储。</summary>
    private sealed class FakeEntries : IEntryCatalog
    {
        public List<(string Scope, List<TranslationUpsertBatchItem> Items)> Batches { get; } = [];

        public Task<IReadOnlyList<TranslationEntry>> UpsertTranslationsBatchAsync(
            string projectCode,
            IReadOnlyList<TranslationUpsertBatchItem> items,
            CancellationToken ct = default)
        {
            lock (Batches)
            {
                Batches.Add((projectCode, items.ToList()));
            }

            var saved = items.Select(i =>
            {
                var entry = new TranslationEntry
                {
                    Key = i.Key,
                    SourceText = i.SourceText,
                    Namespace = new TranslationNamespace { Code = i.NamespaceCode }
                };
                foreach (var (culture, text) in i.Translations)
                {
                    entry.Translations.Add(new TranslationValue
                    {
                        EntryId = entry.Id,
                        Culture = culture,
                        Text = text,
                        Status = i.Status ?? TranslationStatus.Draft
                    });
                }

                return entry;
            }).ToList();

            return Task.FromResult<IReadOnlyList<TranslationEntry>>(saved);
        }

        public Task<TranslationEntry> UpsertEntryAsync(string projectCode, string namespaceCode, string key, string sourceText,
            string? context, int? maxLength, IDictionary<string, string>? translations = null, bool isAutoReported = false,
            TranslationStatus? status = null, bool acknowledgeIssues = false, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<JsonImportResult> ImportJsonBundleAsync(string projectCode, string culture, ReadOnlyMemory<byte> utf8Json,
            string defaultNamespace = "", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteEntryAsync(string projectCode, string namespaceCode, string key, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> RecordMissingKeysAsync(MissingKeyBatchReport report, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MASA.Lingua.Core.Compilation.TranslationLayer>> BuildTranslationLayersAsync(
            string projectCode, string targetCulture, PublishContentMode? contentMode = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static EntryAiTranslator Create(IAiTranslationService ai, IEntryCatalog entries) =>
        new(ai, entries, NullLogger<EntryAiTranslator>.Instance);

    private static TranslationEntry Entry(string key, string source, params (string Culture, string Text, TranslationStatus Status)[] translations)
    {
        var entry = new TranslationEntry
        {
            Key = key,
            SourceText = source,
            Namespace = new TranslationNamespace { Code = "order" }
        };
        foreach (var (culture, text, status) in translations)
        {
            entry.Translations.Add(new TranslationValue { EntryId = entry.Id, Culture = culture, Text = text, Status = status });
        }

        return entry;
    }

    private static string ScopeOf(TranslationEntry _) => "shop-vue";

    // ---------------------------------------------------------------------

    [Fact]
    public async Task Fill_Mode_Only_Sends_Pending_Cultures_And_Skips_Rest()
    {
        var ai = new FakeAi();
        var store = new FakeEntries();
        var translator = Create(ai, store);

        var entry = Entry("save", "保存",
            ("en-US", "Save", TranslationStatus.Approved),  // 已审核：跳过
            ("ja-JP", "ー", TranslationStatus.Draft));      // 草稿：要补

        var saved = new List<TranslationEntry>();
        var outcome = await translator.TranslateAsync(
            [entry], "shop-vue", ["en-US", "ja-JP"], "zh-CN", ScopeOf, [], overwrite: false,
            onSaved: items =>
            {
                saved.AddRange(items);
                return Task.FromResult<IReadOnlyList<TranslationEntry>>([]);
            });

        Assert.Null(outcome.Failure);
        Assert.Equal(1, outcome.Skipped);        // en-US 被跳过
        Assert.Equal(1, outcome.Generated);      // ja-JP 写入一条
        Assert.False(outcome.NeedsReload);

        // en-US 一个请求都不该发（没有待处理项）
        Assert.Equal(["ja-JP"], ai.RequestedCultures);
        Assert.All(ai.RequestedItems, i => Assert.Equal("save", i.Key));

        Assert.Equal("ja-JP", Assert.Single(saved).Translations.Single().Culture);
    }

    [Fact]
    public async Task Overwrite_Translates_Every_Target_Culture()
    {
        var ai = new FakeAi();
        var translator = Create(ai, new FakeEntries());

        var entry = Entry("save", "保存", ("en-US", "Save", TranslationStatus.Approved));

        var outcome = await translator.TranslateAsync(
            [entry], "shop-vue", ["en-US", "ja-JP"], "zh-CN", ScopeOf, [], overwrite: true,
            onSaved: _ => Task.FromResult<IReadOnlyList<TranslationEntry>>([]));

        Assert.Null(outcome.Failure);
        Assert.Equal(0, outcome.Skipped);
        Assert.Equal(2, outcome.Generated);
        Assert.Equal(["en-US", "ja-JP"], ai.RequestedCultures.Order());
    }

    [Fact]
    public async Task One_Culture_Failing_Does_Not_Block_The_Others()
    {
        var ai = new FakeAi(throwOn: ["ja-JP"]);
        var store = new FakeEntries();
        var translator = Create(ai, store);

        var entry = Entry("save", "保存");

        var outcome = await translator.TranslateAsync(
            [entry], "shop-vue", ["en-US", "ja-JP"], "zh-CN", ScopeOf, [], overwrite: true,
            onSaved: _ => Task.FromResult<IReadOnlyList<TranslationEntry>>([]));

        // ja-JP 失败，但 en-US 的译文照常写回——这是「失败不牵连」的核心保证
        Assert.Equal("ja-JP 失败", outcome.Failure);
        Assert.Equal(1, outcome.Generated);
        Assert.Contains(store.Batches, b => b.Items.Any(i => i.Translations.ContainsKey("en-US")));
        Assert.DoesNotContain(store.Batches, b => b.Items.Any(i => i.Translations.ContainsKey("ja-JP")));
    }

    [Fact]
    public async Task Partial_Results_From_A_Failed_Culture_Are_Still_Persisted()
    {
        var ai = new FakeAi(throwOn: ["ja-JP"], partialBeforeThrow: ["ja-JP"]);
        var store = new FakeEntries();
        var translator = Create(ai, store);

        var outcome = await translator.TranslateAsync(
            [Entry("save", "保存")], "shop-vue", ["ja-JP"], "zh-CN", ScopeOf, [], overwrite: true,
            onSaved: _ => Task.FromResult<IReadOnlyList<TranslationEntry>>([]));

        // 失败之前已经拿到的译文不能浪费：照常落库，同时把失败原因带回去
        Assert.Equal("ja-JP 失败", outcome.Failure);
        Assert.Equal(1, outcome.Generated);
        Assert.Contains(store.Batches, b => b.Items.Any(i => i.Translations.ContainsKey("ja-JP")));
    }

    [Fact]
    public async Task Writes_Are_Grouped_By_Scope_And_Blocking_Issues_Go_To_Review()
    {
        var ai = new RejectingAi();
        var store = new FakeEntries();
        var translator = Create(ai, store);

        var projectEntry = Entry("inProject", "项目词条");
        var sharedEntry = Entry("inShared", "共享词条");
        string ScopeFor(TranslationEntry e) => ReferenceEquals(e, sharedEntry) ? WorkspaceScope.Shared : "shop-vue";

        var outcome = await translator.TranslateAsync(
            [projectEntry, sharedEntry], "shop-vue", ["en-US"], "zh-CN", ScopeFor, [], overwrite: true,
            onSaved: _ => Task.FromResult<IReadOnlyList<TranslationEntry>>([]));

        Assert.Null(outcome.Failure);
        Assert.Equal(2, outcome.Generated);
        Assert.Equal(1, outcome.NeedsReview); // 带阻断问题的那个

        // 共享词条与项目词条各成一批，分别写到自己的作用域
        Assert.Equal(2, store.Batches.Count);
        Assert.Contains(store.Batches, b => b.Scope == WorkspaceScope.Shared && b.Items.Single().Key == "inShared");
        var projectBatch = Assert.Single(store.Batches, b => b.Scope == "shop-vue");
        Assert.Equal(TranslationStatus.NeedsReview, projectBatch.Items.Single().Status);
    }

    [Fact]
    public async Task Missing_Entries_Reported_By_Callback_Request_A_Reload()
    {
        var ai = new FakeAi();
        var translator = Create(ai, new FakeEntries());

        var outcome = await translator.TranslateAsync(
            [Entry("save", "保存")], "shop-vue", ["en-US"], "zh-CN", ScopeOf, [], overwrite: true,
            // 模拟「内存词条树里找不到这条词条」：内存与库已不一致
            onSaved: saved => Task.FromResult<IReadOnlyList<TranslationEntry>>(saved.ToList()));

        Assert.True(outcome.NeedsReload);
    }

    [Fact]
    public async Task Callback_Is_Invoked_Once_Per_Culture_That_Saved_Something()
    {
        var ai = new FakeAi();
        var translator = Create(ai, new FakeEntries());

        var calls = 0;
        await translator.TranslateAsync(
            [Entry("save", "保存")], "shop-vue", ["en-US", "ja-JP"], "zh-CN", ScopeOf, [], overwrite: true,
            onSaved: _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<IReadOnlyList<TranslationEntry>>([]);
            });

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cultures_Are_Requested_Concurrently()
    {
        var ai = new FakeAi();
        var translator = Create(ai, new FakeEntries());

        await translator.TranslateAsync(
            [Entry("save", "保存")], "shop-vue", ["en-US", "ja-JP", "ko-KR"], "zh-CN", ScopeOf, [], overwrite: true,
            onSaved: _ => Task.FromResult<IReadOnlyList<TranslationEntry>>([]));

        // 三个语种各 20ms，串行至少 60ms；并发时应当观察到多个同时在途
        Assert.True(ai.MaxConcurrentObserved > 1, $"expected concurrent requests, observed max {ai.MaxConcurrentObserved}");
    }

    [Fact]
    public async Task Empty_Entry_Set_Does_Nothing()
    {
        var ai = new FakeAi();
        var translator = Create(ai, new FakeEntries());

        var outcome = await translator.TranslateAsync(
            [], "shop-vue", ["en-US"], "zh-CN", ScopeOf, [], overwrite: true,
            onSaved: _ => Task.FromResult<IReadOnlyList<TranslationEntry>>([]));

        Assert.Equal(new AiRunOutcome(0, 0, 0, null), outcome);
        Assert.Empty(ai.RequestedCultures);
    }

    /// <summary>返回的译文都带一个阻断级问题（占位符缺失）。</summary>
    private sealed class RejectingAi : IAiTranslationService
    {
        public Task<IReadOnlyList<AiTranslationResultItem>> TranslateBatchAsync(
            string projectCode, string sourceCulture, IReadOnlyList<string> targetCultures,
            IReadOnlyList<AiTranslationRequestItem> items, IReadOnlyList<GlossaryTerm> glossaryTerms,
            CancellationToken ct = default)
        {
            var culture = targetCultures[0];
            IReadOnlyList<AiTranslationResultItem> results = items
                .Select((i, index) => new AiTranslationResultItem(
                    i.EntryId,
                    culture,
                    $"translated {i.Key}",
                    // 第一条带阻断问题，其余干净
                    index == 0
                        ? [new ValidationIssue($"{i.Namespace}.{i.Key}", culture, "PLACEHOLDER_MISSING", IssueSeverity.Error, "缺失占位符")]
                        : []))
                .ToList();
            return Task.FromResult(results);
        }
    }
}
