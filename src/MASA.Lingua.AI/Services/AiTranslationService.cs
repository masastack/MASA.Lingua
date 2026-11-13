using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MASA.Lingua.AI.Services;

/// <summary>
/// 基于 Microsoft.Extensions.AI (IChatClient) 的上下文感知批量翻译服务：
/// 1. 只把本批源文案命中的 Glossary 术语与 MaxLength 长度约束写进提示词；
/// 2. 按 <see cref="LinguaAiOptions.BatchSize"/> 分块请求，避免单次请求过大；
///    块之间按 <see cref="LinguaAiOptions.MaxConcurrency"/> 有限并发，任何一块失败即取消其余请求，已完成的块作为部分结果保留；
/// 3. 译文回来后用 DeterministicTranslationValidator 做占位符、长度、HTML 与术语自检，阻断级问题带着问题清单让模型修复一次；
/// 4. 请求失败或返回内容无法使用时一律抛出 <see cref="AiTranslationException"/>，不会用任何本地替代内容兜底。
/// </summary>
public sealed class AiTranslationService(
    DeterministicTranslationValidator validator,
    ILogger<AiTranslationService> logger,
    IChatClient chatClient,
    IOptions<LinguaAiOptions> options) : IAiTranslationService
{
    // 部分推理模型会把思考过程以 <think>…</think> 夹在正文里，思考内容里常带 {count} 之类的花括号，必须先剥掉再找 JSON
    private static readonly Regex ThinkBlocks = new("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex WhitespaceRuns = new(@"\s+");

    private readonly LinguaAiOptions _options = options.Value;

    /// <summary>在途模型请求数的上限（实例级；服务注册为单例，因此限制的是整个站点）。</summary>
    private readonly SemaphoreSlim _requestGate = new(Math.Max(1, options.Value.MaxConcurrency));

    // 注意：刻意不实现 IDisposable。SemaphoreSlim 只有在访问 AvailableWaitHandle 时才会分配等待句柄，这里没有，
    // 所以没有需要释放的非托管资源；而在容器关闭时 Dispose 它，反而会让仍在途的请求在 finally 里的 Release()
    // 抛 ObjectDisposedException（盖掉真正的失败原因）。随服务实例一起被 GC 回收即可。

    public async Task<IReadOnlyList<AiTranslationResultItem>> TranslateBatchAsync(
        string projectCode,
        string sourceCulture,
        IReadOnlyList<string> targetCultures,
        IReadOnlyList<AiTranslationRequestItem> items,
        IReadOnlyList<GlossaryTerm> glossaryTerms,
        CancellationToken ct = default)
    {
        var results = new List<AiTranslationResultItem>();
        if (items.Count == 0 || targetCultures.Count == 0)
        {
            return results;
        }

        var batchSize = Math.Max(1, _options.BatchSize);

        // 工作单元 = (目标语种, 一块词条)，顺序即「语种声明顺序 × 块顺序」，结果也按这个顺序汇总
        var work = new List<(string Culture, AiTranslationRequestItem[] Chunk)>();
        foreach (var targetCulture in targetCultures)
        {
            if (string.Equals(sourceCulture, targetCulture, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var chunk in items.Chunk(batchSize))
            {
                work.Add((targetCulture, chunk));
            }
        }

        // 块之间有限并发：同一时刻在途的模型请求数受 MaxConcurrency 限制（门是实例级的，服务是单例，
        // 所以限制的是整个站点而不是单次调用，多个管理员同时点批量补全也不会把模型端打爆）。
        // 任何一块失败：不再发起后面的请求，并取消在途请求（服务不可用时继续只会重复失败）；
        // 失败之前已完成的块照常作为部分结果保留，由调用方写回。
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var outputs = new IReadOnlyList<AiTranslationResultItem>?[work.Count];
        var failures = new SortedDictionary<int, AiTranslationException>();
        var failureLock = new object();

        void RecordFailure(int index, Exception? inner, string message)
        {
            lock (failureLock)
            {
                // 取消其余请求之后，别的块多半是因取消而中止（不记为失败）；只有真正失败的才会走到这里
                failures[index] = new AiTranslationException(message, inner, []);
            }

            stop.Cancel();
        }

        async Task RunAsync(int index)
        {
            var (culture, chunk) = work[index];
            try
            {
                await _requestGate.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                return; // 等位期间调用方取消，或前面的块已失败：这一块不发起
            }

            try
            {
                stop.Token.ThrowIfCancellationRequested();

                var translated = await TranslateChunkAsync(
                    projectCode, sourceCulture, culture, chunk, glossaryTerms, stop.Token);

                if (translated.Count == 0)
                {
                    // 整块都没拿到可用译文说明模型没按约定输出，继续请求后面的块只会重复失败
                    RecordFailure(
                        index,
                        null,
                        $"AI 服务调用失败（{DescribeTarget()}）：模型返回的内容里找不到任何词条的译文。" +
                        "请确认该模型能按要求输出「id → 译文」的 JSON，或换一个模型。");
                    return;
                }

                outputs[index] = translated;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // 调用方取消，或别的块失败后被取消：不是这一块自己的失败
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI 翻译请求失败：项目 {Project}，目标语种 {Culture}", projectCode, culture);
                RecordFailure(index, ex, Describe(ex));
            }
            finally
            {
                _requestGate.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, work.Count).Select(RunAsync));

        ct.ThrowIfCancellationRequested();

        var missing = 0;
        for (var i = 0; i < work.Count; i++)
        {
            if (outputs[i] is { } translated)
            {
                results.AddRange(translated);
                missing += work[i].Chunk.Length - translated.Count;
            }
        }

        if (failures.Count > 0)
        {
            // 同时失败多块时报最靠前的那块（与串行执行时第一个遇到的失败一致）
            var first = failures.First().Value;
            throw new AiTranslationException(first.Message, first.InnerException, results);
        }

        if (missing > 0)
        {
            throw new AiTranslationException(
                $"模型漏返了 {missing} 条译文，已拿到的 {results.Count} 条会保留。可以再试一次；如果经常发生，请调小 Lingua:AI:BatchSize。",
                null,
                results);
        }

        return results;
    }

    private async Task<IReadOnlyList<AiTranslationResultItem>> TranslateChunkAsync(
        string projectCode,
        string sourceCulture,
        string targetCulture,
        AiTranslationRequestItem[] chunk,
        IReadOnlyList<GlossaryTerm> glossaryTerms,
        CancellationToken ct)
    {
        // 只把本块源文案实际命中的术语注入 Prompt，避免术语库变大后浪费 token、稀释约束
        var relevantTerms = SelectRelevantTerms(chunk, glossaryTerms);

        var llmOutputs = await TranslateWithChatClientAsync(
            projectCode,
            sourceCulture,
            targetCulture,
            chunk,
            relevantTerms,
            ct);

        var drafts = new List<TranslationDraft>(chunk.Length);
        foreach (var item in chunk)
        {
            if (!llmOutputs.TryGetValue(item.EntryId, out var text) || string.IsNullOrWhiteSpace(text))
            {
                // 模型漏返的词条不凑数，由调用方统计并提示
                continue;
            }

            // 校验只需要词条属性与命名空间编码（用于问题定位），这里用未跟踪的临时实体承载，不写库
            var tempEntry = new TranslationEntry
            {
                Id = item.EntryId,
                Namespace = new TranslationNamespace { Code = item.Namespace },
                Key = item.Key,
                SourceText = item.SourceText,
                Context = item.Context,
                MaxLength = item.MaxLength
            };

            var issues = validator.ValidateEntry(tempEntry, targetCulture, text, glossaryTerms);
            drafts.Add(new TranslationDraft(item, tempEntry, text, issues));
        }

        // LLM 输出违反阻断级规则（占位符/长度/HTML/术语）时，带着具体问题与建议修复值做一次修复重试
        var failed = drafts.Where(d => d.Issues.Any(i => i.IsBlocking)).ToList();
        if (failed.Count > 0)
        {
            await TryRepairAsync(projectCode, sourceCulture, targetCulture, failed, relevantTerms, glossaryTerms, ct);
        }

        return drafts
            .Select(d => new AiTranslationResultItem(d.Item.EntryId, targetCulture, d.Text, d.Issues))
            .ToList();
    }

    private sealed class TranslationDraft(
        AiTranslationRequestItem item,
        TranslationEntry entry,
        string text,
        IReadOnlyList<ValidationIssue> issues)
    {
        public AiTranslationRequestItem Item { get; } = item;
        public TranslationEntry Entry { get; } = entry;
        public string Text { get; set; } = text;
        public IReadOnlyList<ValidationIssue> Issues { get; set; } = issues;
    }

    private async Task TryRepairAsync(
        string projectCode,
        string sourceCulture,
        string targetCulture,
        List<TranslationDraft> failed,
        IReadOnlyList<GlossaryTerm> relevantTerms,
        IReadOnlyList<GlossaryTerm> allTerms,
        CancellationToken ct)
    {
        try
        {
            var repaired = await RepairWithChatClientAsync(
                projectCode, sourceCulture, targetCulture, failed, relevantTerms, ct);

            foreach (var draft in failed)
            {
                if (!repaired.TryGetValue(draft.Item.EntryId, out var fixedText) || string.IsNullOrWhiteSpace(fixedText))
                {
                    continue;
                }

                var newIssues = validator.ValidateEntry(draft.Entry, targetCulture, fixedText, allTerms);

                // 仅当修复结果确实更好（阻断问题更少，或阻断数相同但总问题更少）时才采用
                var better = newIssues.Count(i => i.IsBlocking) < draft.Issues.Count(i => i.IsBlocking) ||
                             (newIssues.Count(i => i.IsBlocking) == draft.Issues.Count(i => i.IsBlocking) &&
                              newIssues.Count < draft.Issues.Count);
                if (better)
                {
                    draft.Text = fixedText;
                    draft.Issues = newIssues;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 修复只是锦上添花：失败时保留模型原本的译文（它是真实的模型输出），带着阻断问题交给调用方标为待复核
            logger.LogWarning(ex, "AI 译文自动修复重试失败，保留原译文并交由人工复核。");
        }
    }

    /// <summary>
    /// 仅挑选在本批任意源文案中命中的术语（命中判定与规则校验器一致）。
    /// </summary>
    private static IReadOnlyList<GlossaryTerm> SelectRelevantTerms(
        IReadOnlyList<AiTranslationRequestItem> items,
        IReadOnlyList<GlossaryTerm> glossaryTerms)
    {
        if (glossaryTerms.Count == 0)
        {
            return glossaryTerms;
        }

        var hit = new HashSet<GlossaryTerm>();
        foreach (var item in items)
        {
            foreach (var term in GlossaryMatcher.FindMatchedTerms(item.SourceText, glossaryTerms))
            {
                hit.Add(term);
            }
        }

        return glossaryTerms.Where(hit.Contains).ToList();
    }

    private async Task<Dictionary<Guid, string>> TranslateWithChatClientAsync(
        string projectCode,
        string sourceCulture,
        string targetCulture,
        IReadOnlyList<AiTranslationRequestItem> items,
        IReadOnlyList<GlossaryTerm> glossaryTerms,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are an expert localization engine for project '{projectCode}'.");
        sb.AppendLine($"Translate the following UI strings from '{sourceCulture}' to '{targetCulture}'.");
        sb.AppendLine("CRITICAL RULES:");
        sb.AppendLine("1. Keep all placeholders like {userName}, {count}, {days}, {0} 100% intact.");
        sb.AppendLine("2. Respect MaxLength constraints strictly.");
        AppendGlossaryRules(sb, targetCulture, glossaryTerms);

        sb.AppendLine();
        sb.AppendLine("Return ONLY a valid JSON object mapping each EntryId to its translated string.");
        sb.AppendLine("Items to translate:");
        sb.AppendLine(JsonSerializer.Serialize(items.Select(i => new
        {
            id = i.EntryId,
            key = $"{i.Namespace}.{i.Key}",
            source = i.SourceText,
            context = i.Context,
            maxLength = i.MaxLength
        })));

        var response = await chatClient!.GetResponseAsync(sb.ToString(), cancellationToken: ct);
        return ParseIdMap(response.Text);
    }

    private async Task<Dictionary<Guid, string>> RepairWithChatClientAsync(
        string projectCode,
        string sourceCulture,
        string targetCulture,
        IReadOnlyList<TranslationDraft> failed,
        IReadOnlyList<GlossaryTerm> relevantTerms,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are an expert localization engine for project '{projectCode}'.");
        sb.AppendLine($"Your previous translations from '{sourceCulture}' to '{targetCulture}' violated localization rules.");
        sb.AppendLine("Fix every problem listed for each item and return a corrected translation.");
        sb.AppendLine("CRITICAL RULES:");
        sb.AppendLine("1. Keep all placeholders like {userName}, {count}, {days}, {0} 100% intact.");
        sb.AppendLine("2. Respect MaxLength constraints strictly.");
        AppendGlossaryRules(sb, targetCulture, relevantTerms);

        sb.AppendLine();
        sb.AppendLine("Return ONLY a valid JSON object mapping each id to its corrected translated string.");
        sb.AppendLine("Items to fix:");
        sb.AppendLine(JsonSerializer.Serialize(failed.Select(d => new
        {
            id = d.Item.EntryId,
            key = $"{d.Item.Namespace}.{d.Item.Key}",
            source = d.Item.SourceText,
            context = d.Item.Context,
            maxLength = d.Item.MaxLength,
            previousTranslation = d.Text,
            problems = d.Issues.Where(i => i.IsBlocking).Select(i => i.Message),
            suggestedFixes = d.Issues.Where(i => i.IsBlocking && i.SuggestedFix is not null).Select(i => i.SuggestedFix)
        })));

        var response = await chatClient!.GetResponseAsync(sb.ToString(), cancellationToken: ct);
        return ParseIdMap(response.Text);
    }

    private static void AppendGlossaryRules(
        StringBuilder sb,
        string targetCulture,
        IReadOnlyList<GlossaryTerm> glossaryTerms)
    {
        sb.AppendLine("3. Follow the Mandatory Glossary below:");

        var written = 0;
        foreach (var g in glossaryTerms)
        {
            if (g.DoNotTranslate)
            {
                sb.AppendLine($"   - \"{g.SourceTerm}\": DO NOT TRANSLATE (keep as \"{g.SourceTerm}\")");
                written++;
            }
            else
            {
                var hasTarget = g.TargetTranslations.TryGetValue(targetCulture, out var targetTerm);
                var forbidden = g.ForbiddenTranslations.TryGetValue(targetCulture, out var banned)
                    ? banned.Where(w => !string.IsNullOrWhiteSpace(w)).ToList()
                    : [];

                if (hasTarget)
                {
                    sb.AppendLine($"   - \"{g.SourceTerm}\" -> MUST translate as \"{targetTerm}\"");
                    written++;
                }

                if (forbidden.Count > 0)
                {
                    sb.AppendLine($"   - \"{g.SourceTerm}\" -> NEVER translate as {string.Join(", ", forbidden.Select(w => $"\"{w}\""))}");
                    if (!hasTarget)
                    {
                        written++;
                    }
                }
            }
        }

        if (written == 0)
        {
            sb.AppendLine("   (no glossary terms apply to these strings)");
        }
    }

    /// <summary>
    /// 从模型回复里取出「词条 id → 译文」的 JSON：先剥掉 &lt;think&gt; 块，再取首个 { 到最后一个 } 之间的内容
    /// （兼容 ```json 围栏与前后说明文字）。格式不对就抛 <see cref="ModelReplyException"/>，由调用方转成可读的失败信息。
    /// </summary>
    private static Dictionary<Guid, string> ParseIdMap(string? text)
    {
        var raw = ThinkBlocks.Replace(text ?? string.Empty, string.Empty).Trim();
        var startIdx = raw.IndexOf('{');
        var endIdx = raw.LastIndexOf('}');
        if (startIdx < 0 || endIdx <= startIdx)
        {
            throw new ModelReplyException($"模型返回的内容里没有 JSON 对象。返回内容开头：{Preview(raw)}");
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<Guid, string>>(raw[startIdx..(endIdx + 1)])
                   ?? new Dictionary<Guid, string>();
        }
        catch (JsonException ex)
        {
            throw new ModelReplyException(
                $"模型返回的 JSON 无法解析为「词条 id → 译文」（{ex.Message}）。返回内容开头：{Preview(raw)}");
        }
    }

    /// <summary>把异常变成能直接给用户看的一句话，带上模型与服务地址，方便一眼看出是哪套配置出了问题。</summary>
    private string Describe(Exception ex)
    {
        var reason = ex switch
        {
            ModelReplyException => ex.Message,
            OperationCanceledException => "请求超时，模型响应太慢。可以调小 Lingua:AI:BatchSize 后重试。",
            AggregateException { InnerExceptions.Count: > 0 } retried => DescribeRetries(retried),
            _ => Preview(ex.Message, 300)
        };

        return $"AI 服务调用失败（{DescribeTarget()}）：{reason}";
    }

    /// <summary>
    /// OpenAI SDK 的重试策略耗尽后会抛 <see cref="AggregateException"/>，它的 Message 把每次失败的原因逐条拼在一起
    /// （连不上服务时通常是同一句话重复 N 遍）。这里去重，只告诉用户尝试了几次、真正的原因是什么。
    /// </summary>
    private static string DescribeRetries(AggregateException aggregate)
    {
        var attempts = aggregate.Flatten().InnerExceptions;
        var reasons = attempts.Select(e => Preview(e.Message, 200)).Distinct().ToList();
        return $"连续尝试 {attempts.Count} 次均失败：{string.Join("；", reasons)}";
    }

    private string DescribeTarget()
    {
        var model = string.IsNullOrWhiteSpace(_options.ModelId) ? "未指定模型" : _options.ModelId;
        return Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var uri)
            ? $"{model} @ {uri.Authority}"
            : model;
    }

    private static string Preview(string text, int maxLength = 120)
    {
        var flat = WhitespaceRuns.Replace(text, " ").Trim();
        return flat.Length <= maxLength ? flat : flat[..maxLength] + "…";
    }

    /// <summary>模型回了内容，但不是约定的 JSON 映射。</summary>
    private sealed class ModelReplyException(string message) : Exception(message);
}
