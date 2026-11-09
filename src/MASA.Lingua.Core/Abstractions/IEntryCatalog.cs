using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Abstractions;

/// <summary>
/// 批量写入的单条请求，字段与 <see cref="IEntryCatalog.UpsertEntryAsync"/> 的参数一一对应。
/// <paramref name="Translations"/> 通常只含一个目标语种（AI 批量补全按语种分批调用）。
/// </summary>
public sealed record TranslationUpsertBatchItem(
    string NamespaceCode,
    string Key,
    string SourceText,
    string? Context,
    int? MaxLength,
    IReadOnlyDictionary<string, string> Translations,
    TranslationStatus? Status,
    bool AcknowledgeIssues = false);

/// <summary>
/// 词条主体：词条写入（手工编辑 / JSON 导入 / 缺失词条上报）与「发布用译文图层」的构建。
/// <para>
/// 切分理由：写入方（管理页、导入、上报端点）只需要改变词条与译文，不该看到发布快照与项目配置；
/// 图层构建是同一主体的读取视图（把共享/项目、回退/目标四类译文合成一条链），与词条强相关，故同置一处。
/// 写入遵循统一校验：要以「已审核 / AI 已译」入库的译文先过占位符 / 长度 / HTML / 术语校验，
/// 违规时降级为待复核并写明原因（<c>acknowledgeIssues = true</c> 表示人工已确认）。
/// </para>
/// </summary>
public interface IEntryCatalog
{
    Task<TranslationEntry> UpsertEntryAsync(
        string projectCode,
        string namespaceCode,
        string key,
        string sourceText,
        string? context,
        int? maxLength,
        IDictionary<string, string>? translations = null,
        bool isAutoReported = false,
        TranslationStatus? status = null,
        bool acknowledgeIssues = false,
        CancellationToken ct = default);

    Task<JsonImportResult> ImportJsonBundleAsync(
        string projectCode,
        string culture,
        ReadOnlyMemory<byte> utf8Json,
        string defaultNamespace = "",
        CancellationToken ct = default);

    /// <summary>
    /// 一次批量写入多条译文（与 <see cref="UpsertEntryAsync"/> 单条语义逐项相同）：
    /// 同一个 DbContext、同一个事务，词条或命名空间不存在时按需创建，
    /// 每条译文过同样的写入校验（违规降级为待复核并写明原因）。
    /// 供 AI 批量补全等一次写多条译文的调用方使用，避免逐条各开一个事务。
    /// </summary>
    Task<IReadOnlyList<TranslationEntry>> UpsertTranslationsBatchAsync(
        string projectCode,
        IReadOnlyList<TranslationUpsertBatchItem> items,
        CancellationToken ct = default);

    /// <summary>
    /// 删除一条词条，连同其全部语种的译文；命名空间因此变空时一并移除空壳。
    /// <paramref name="projectCode"/> 传 <c>__shared__</c> 表示操作共享命名空间里的词条。
    /// 返回是否真的删除了词条（命名空间或 Key 不存在时返回 <c>false</c>，调用方可据此提示）。
    /// </summary>
    Task<bool> DeleteEntryAsync(
        string projectCode,
        string namespaceCode,
        string key,
        CancellationToken ct = default);

    Task<int> RecordMissingKeysAsync(MissingKeyBatchReport report, CancellationToken ct = default);

    /// <summary>
    /// 按优先级构建指定项目与语种的合并图层（SharedFallback -> SharedTarget -> ProjectFallback -> ProjectTarget）
    /// </summary>
    /// <param name="contentMode">
    /// 非默认语种的译文按状态过滤：null 表示不过滤（草稿视图，用于导出与对比）；
    /// 传入项目发布策略的 <see cref="PublishContentMode"/> 则只包含会上线的译文，其余回退到默认语种。
    /// </param>
    Task<IReadOnlyList<TranslationLayer>> BuildTranslationLayersAsync(
        string projectCode,
        string targetCulture,
        PublishContentMode? contentMode = null,
        CancellationToken ct = default);
}
