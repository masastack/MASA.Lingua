using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Server.Components.Shared;
using Microsoft.Extensions.Logging;

namespace MASA.Lingua.Server.Services;

/// <summary>一次 AI 翻译（单条或批量）的结果：写入条数、其中需复核的条数、跳过的条数，以及失败原因（成功为 null）。</summary>
public sealed record AiRunOutcome(int Generated, int NeedsReview, int Skipped, string? Failure)
{
    /// <summary>为 true 时内存词条树与库已不一致（写回的词条在内存里找不到），调用方应整页重载而不是增量更新。</summary>
    public bool NeedsReload { get; init; }
}

/// <summary>
/// 词条矩阵的 AI 翻译编排：把「哪些词条 × 哪些语种」拆成按语种的并发请求，把结果按归属批量写回，
/// 最后交回调用方一份纯结果。页面只负责禁用按钮、toast 提示与之后的增量校验，不再自己管并发与分组。
/// <para>
/// 为什么按语种而不是按词条分批：一次模型请求里混多个目标语种，提示词与返回格式都要带语种维度，
/// 模型漏返 / 串语种的概率明显更高；按语种各发一批，失败也只影响一个语种，其余语种的结果照常落库。
/// </para>
/// <para>
/// 写回必须串行（<c>persistGate</c>）：每个语种写回时都会重新读库、返回新的词条实例，
/// 两个语种同时写回同一词条时，后一个读到的可能还没包含前一个的译文，替换后会把前一个语种从内存里抹掉。
/// 并发的是 AI 请求（慢），写回（快）排队即可。
/// </para>
/// </summary>
public sealed class EntryAiTranslator(
    IAiTranslationService aiService,
    IEntryCatalog entries,
    ILogger<EntryAiTranslator> logger)
{
    /// <summary>
    /// 翻译一批词条。返回的结果里 <see cref="AiRunOutcome.Failure"/> 非 null 表示有语种失败，
    /// 但已经成功的部分**已经落库**并随 <paramref name="onSaved"/> 交给调用方，调用方不应重复请求。
    /// </summary>
    /// <param name="entriesToTranslate">要翻译的词条（已按页面当前范围筛过）。</param>
    /// <param name="projectCode">提示词里用的项目编码（页面当前选中的项目或 <c>__shared__</c>）。</param>
    /// <param name="targetCultures">目标语种（不含源语种）。</param>
    /// <param name="sourceCulture">源语种，用于提示词与默认语种回退。</param>
    /// <param name="scopeOf">词条归属的仓储作用域：共享词条走 <c>__shared__</c>，其余走项目编码。写回按作用域分批。</param>
    /// <param name="overwrite">true = 重新翻译全部；false = 只补缺失 / 草稿 / 待复核。</param>
    /// <param name="onSaved">
    /// 每批写回成功后调用（可能多次，在后台线程上），参数是本批保存后的词条。
    /// **实现必须在组件自己的同步上下文里执行**（例如 <c>InvokeAsync</c>）——它要改的是页面状态。
    /// 返回值是「在内存词条树里找不到的词条」：非空表示内存与库已不一致，调用方应改为整页重载。
    /// </param>
    /// <param name="ct">取消标记。</param>
    public async Task<AiRunOutcome> TranslateAsync(
        IReadOnlyList<TranslationEntry> entriesToTranslate,
        string projectCode,
        IReadOnlyList<string> targetCultures,
        string sourceCulture,
        Func<TranslationEntry, string> scopeOf,
        IReadOnlyList<GlossaryTerm> glossaryTerms,
        bool overwrite,
        Func<IReadOnlyCollection<TranslationEntry>, Task<IReadOnlyList<TranslationEntry>>> onSaved,
        CancellationToken ct = default)
    {
        var entryMap = entriesToTranslate.ToDictionary(e => e.Id);
        var cultures = targetCultures.ToList();

        int generated = 0, needsReview = 0, skipped = 0;
        int needsReload = 0;
        var failures = new string?[cultures.Count]; // 按语种声明顺序存放，保证报告的失败原因稳定

        using var persistGate = new SemaphoreSlim(1, 1);

        async Task TranslateCultureAsync(int order, string culture)
        {
            var pending = overwrite
                ? entriesToTranslate.ToList()
                : entriesToTranslate.Where(e => EntryMatrix.NeedsAi(e, culture)).ToList();
            // 各语种并发执行：跳过计数用原子加，否则并发累加会丢更新
            Interlocked.Add(ref skipped, entriesToTranslate.Count - pending.Count);
            if (pending.Count == 0)
            {
                return;
            }

            var items = pending.Select(e => new AiTranslationRequestItem(
                e.Id,
                e.NamespaceCode,
                e.Key,
                e.SourceText,
                e.Context,
                e.MaxLength)).ToList();

            IReadOnlyList<AiTranslationResultItem> aiResults;
            try
            {
                aiResults = await aiService.TranslateBatchAsync(projectCode, sourceCulture, [culture], items, glossaryTerms, ct);
            }
            catch (AiTranslationException ex)
            {
                aiResults = ex.PartialResults;
                failures[order] = ex.Message;
                logger.LogWarning(ex, "AI 翻译失败：目标语种 {Culture}（已保留 {Count} 条已完成的译文）", culture, aiResults.Count);
            }

            await persistGate.WaitAsync(ct);
            try
            {
                var (savedCount, savedNeedsReview, savedEntries) = await PersistAsync(aiResults, entryMap, scopeOf);
                // 写回已在门内串行，但计数只跨线程读写，用原子加让「串行」这件事不依赖信号量的内存语义
                Interlocked.Add(ref generated, savedCount);
                Interlocked.Add(ref needsReview, savedNeedsReview);

                if (savedEntries.Count > 0)
                {
                    // 一批只扫一遍词条树（逐条替换是 O(写回条数 × 词条总数)）
                    if ((await onSaved(savedEntries)).Count > 0)
                    {
                        Interlocked.Exchange(ref needsReload, 1);
                    }
                }
            }
            finally
            {
                persistGate.Release();
            }
        }

        // WhenAll 会等所有语种都结束才返回：写回不会与仍在进行的语种交错
        await Task.WhenAll(cultures.Select((culture, order) => TranslateCultureAsync(order, culture)));

        return new AiRunOutcome(generated, needsReview, skipped, failures.FirstOrDefault(f => f is not null))
        {
            NeedsReload = Volatile.Read(ref needsReload) == 1,
        };
    }

    /// <summary>
    /// 把 AI 译文按归属批量写回：共享与项目命名空间各一批（一个 DbContext + 一个事务），不再逐条各开一个事务；
    /// 含阻断级问题（占位符 / 长度 / HTML 错误或术语库违规）的译文只能进入「待复核」，不直接标记为已通过。
    /// </summary>
    private async Task<(int Generated, int NeedsReview, List<TranslationEntry> Saved)> PersistAsync(
        IReadOnlyList<AiTranslationResultItem> aiResults,
        IReadOnlyDictionary<Guid, TranslationEntry> entryMap,
        Func<TranslationEntry, string> scopeOf)
    {
        int needsReview = 0;
        var saved = new List<TranslationEntry>();
        var byScope = new Dictionary<string, List<TranslationUpsertBatchItem>>(StringComparer.Ordinal);

        foreach (var res in aiResults)
        {
            if (!entryMap.TryGetValue(res.EntryId, out var entry))
            {
                continue;
            }

            var targetScope = scopeOf(entry);
            var blocking = res.ValidationIssues.Any(i => i.IsBlocking);
            if (blocking)
            {
                needsReview++;
            }

            var item = new TranslationUpsertBatchItem(
                entry.NamespaceCode,
                entry.Key,
                entry.SourceText,
                entry.Context,
                entry.MaxLength,
                new Dictionary<string, string> { [res.TargetCulture] = res.TranslatedText },
                blocking ? TranslationStatus.NeedsReview : TranslationStatus.AiTranslated);

            if (!byScope.TryGetValue(targetScope, out var batch))
            {
                batch = [];
                byScope[targetScope] = batch;
            }

            batch.Add(item);
        }

        foreach (var (scope, batch) in byScope)
        {
            saved.AddRange(await entries.UpsertTranslationsBatchAsync(scope, batch));
        }

        return (saved.Count, needsReview, saved);
    }
}
