using System.Text.Json.Serialization;

namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 哪些状态的译文会进入对外发布的 {culture}.json。
/// 默认语种（源语种）的内容始终发布；其他语种里不满足条件的译文不会进入发布文件，由回退链回退到默认语种文案。
/// </summary>
/// <remarks>作为 owned JSON 值对象的一部分落库，序列化成字符串，重排枚举值不会静默改变历史数据的语义。</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<PublishContentMode>))]
public enum PublishContentMode
{
    /// <summary>仅人工审核通过的译文。</summary>
    ApprovedOnly = 0,

    /// <summary>已审核 + AI 译文（且已通过规则与术语校验）。草稿与待复核不发布。默认值。</summary>
    ApprovedAndAi = 1,

    /// <summary>全部译文，包括草稿与待复核（等同于旧行为，不推荐）。</summary>
    All = 2
}

/// <summary>
/// 项目级发布策略：决定发布文件包含什么，以及发布前的门禁条件。
/// </summary>
public sealed class PublishPolicy
{
    public PublishContentMode Content { get; set; } = PublishContentMode.ApprovedAndAi;

    /// <summary>各非默认语种「上线完成度」的最低百分比（0 表示不限制）。上线完成度 = 会进入发布文件的译文数 / 词条总数。</summary>
    public int MinCoveragePercent { get; set; }

    /// <summary>即将上线的译文含 Error 级问题（占位符缺失 / 多余、超长、HTML 标签不一致）时是否阻断发布。</summary>
    public bool BlockOnErrors { get; set; } = true;

    /// <summary>即将上线的译文违反术语库（品牌词被翻译、标准译法不一致）时是否阻断发布。</summary>
    public bool BlockOnGlossaryViolations { get; set; } = true;

    /// <summary>
    /// 键路径冲突（如项目根裸 key 与同名命名空间共存：发布 JSON 的同一位置无法既是字符串又是对象）时是否阻断发布。
    /// 冲突的词条文案在嵌套发布 JSON 里会丢失一侧（扁平文件两侧都保留）；.NET 客户端按扁平格式下载、两侧都能取到，
    /// 这类项目可以关掉本开关——嵌套文件仍会少一侧内容，但不再拦截发布。
    /// </summary>
    public bool BlockOnKeyPathConflicts { get; set; } = true;

    public bool Includes(TranslationStatus status) => Content switch
    {
        PublishContentMode.ApprovedOnly => status == TranslationStatus.Approved,
        PublishContentMode.ApprovedAndAi => status is TranslationStatus.Approved or TranslationStatus.AiTranslated,
        _ => true
    };

    /// <summary>把用户输入收敛到合法范围。</summary>
    public PublishPolicy Normalized()
    {
        MinCoveragePercent = Math.Clamp(MinCoveragePercent, 0, 100);
        if (!Enum.IsDefined(Content))
        {
            Content = PublishContentMode.ApprovedAndAi;
        }

        return this;
    }

    public PublishPolicy Clone() => (PublishPolicy)MemberwiseClone();
}
