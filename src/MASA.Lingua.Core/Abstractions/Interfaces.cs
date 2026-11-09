using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Validation;

namespace MASA.Lingua.Core.Abstractions;

public sealed record MissingKeyReportItem(
    string Namespace,
    string Key,
    string? DefaultText = null,
    string? Context = null);

public sealed record MissingKeyBatchReport(
    string ProjectCode,
    string Culture,
    IReadOnlyList<MissingKeyReportItem> Items);

public sealed record JsonImportResult(
    string ProjectCode,
    string Culture,
    int TotalParsed,
    int AddedEntries,
    int UpdatedTranslations,
    IReadOnlyList<string> AffectedNamespaces,
    /// <summary>存在规则/术语校验问题的条数。导入一律按「已审核」入库，问题仅统计提示，发布门禁会把关。</summary>
    int FlaggedIssues = 0,
    int StaleMarked = 0);

public sealed record AiTranslationRequestItem(
    Guid EntryId,
    string Namespace,
    string Key,
    string SourceText,
    string? Context,
    int? MaxLength);

public sealed record AiTranslationResultItem(
    Guid EntryId,
    string TargetCulture,
    string TranslatedText,
    IReadOnlyList<ValidationIssue> ValidationIssues);

/// <summary>
/// AI 翻译调用失败：服务未配置、请求失败（网络 / 鉴权 / 模型不存在 / 超时）、返回内容无法解析，或模型漏返了部分词条。
/// Message 可直接展示给用户；PartialResults 是失败之前已经成功拿到的译文，调用方应先落库再提示，避免浪费已完成的请求。
/// </summary>
public sealed class AiTranslationException(
    string message,
    Exception? innerException = null,
    IReadOnlyList<AiTranslationResultItem>? partialResults = null) : Exception(message, innerException)
{
    public IReadOnlyList<AiTranslationResultItem> PartialResults { get; } = partialResults ?? [];
}

/// <summary>
/// 术语库保存校验失败（供管理页面直接展示 Message）
/// </summary>
public sealed class GlossaryValidationException(string message) : Exception(message);

/// <summary>
/// 写入冲突：目标行已被其他会话修改（数据库并发令牌不匹配）。
/// Message 可直接展示给用户，提示刷新后重试。
/// </summary>
public sealed class LinguaConcurrencyException(string message, Exception innerException)
    : Exception(message, innerException);

/// <summary>
/// 项目轻量摘要（不含词条）：热路径（bundle / manifest 请求）用它校验项目与语种，避免整库加载。
/// </summary>
public sealed record ProjectSummary(
    string Code,
    string DefaultCulture,
    IReadOnlyList<string> SupportedCultures,
    DateTimeOffset? LastPublishedAt,
    string? LastPublishedVersionHash);

/// <summary>
/// 仓储门面：按数据主体拆出的四个接口的组合，**不新增任何成员**。
/// <para>
/// 用途仅限两类调用方：Blazor 管理页（一个页面同时读写多个主体，逐个注入四个接口没有收益）
/// 与测试（直接取门面构造被测对象）。服务端管道类应注入自己真正需要的主体接口
/// （<see cref="IProjectCatalog"/> / <see cref="IEntryCatalog"/> / <see cref="IGlossaryCatalog"/> / <see cref="IPublishArchive"/>），
/// 四者与本门面解析到同一个实例。
/// </para>
/// </summary>
public interface ILinguaRepository : IProjectCatalog, IEntryCatalog, IGlossaryCatalog, IPublishArchive;

public interface IAiTranslationService
{
    /// <summary>
    /// 把「词条 × 目标语种」交给大模型翻译。
    /// 契约：要么返回全部请求项的译文，要么抛出 <see cref="AiTranslationException"/>（其 PartialResults 带已完成的部分）；
    /// 任何情况下都不会用占位内容或本地规则替换来兜底。
    /// </summary>
    Task<IReadOnlyList<AiTranslationResultItem>> TranslateBatchAsync(
        string projectCode,
        string sourceCulture,
        IReadOnlyList<string> targetCultures,
        IReadOnlyList<AiTranslationRequestItem> items,
        IReadOnlyList<GlossaryTerm> glossaryTerms,
        CancellationToken ct = default);
}
