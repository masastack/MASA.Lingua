using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Abstractions;

/// <summary>
/// 术语主体：术语的读写，以及「全局术语 + 项目术语」的生效合并。
/// <para>
/// 切分理由：术语既是人工维护的资源配置（管理页），也是 AI 翻译与规则校验的输入（只读）。
/// 合并规则（同名时项目术语完整覆盖全局术语）是这一主体的核心不变量，因此与术语的增删改放在同一个接口，
/// 避免调用方拿到术语却用错合并口径。
/// </para>
/// </summary>
public interface IGlossaryCatalog
{
    /// <summary>
    /// 保存（新增或更新）术语。校验失败（空术语、同作用域重复、非 DNT 却没有任何目标译法 / 禁用译法、禁用译法与标准译法冲突）时抛出 <see cref="GlossaryValidationException"/>。
    /// </summary>
    Task<GlossaryTerm> SaveGlossaryTermAsync(GlossaryTerm term, CancellationToken ct = default);

    Task DeleteGlossaryTermAsync(Guid termId, CancellationToken ct = default);

    /// <summary>
    /// 获取指定项目的「生效术语」：全局术语与项目术语合并，同名（SourceTerm）时项目术语完整覆盖全局术语。
    /// projectId 为 null 时仅返回全局术语。用于 AI 翻译与规则校验。
    /// </summary>
    Task<IReadOnlyList<GlossaryTerm>> GetGlossaryTermsAsync(Guid? projectId = null, CancellationToken ct = default);

    /// <summary>按项目编码获取生效术语（全局 + 项目，项目覆盖同名全局）；项目不存在返回 null。供只读 glossary 接口与 CLI 使用。</summary>
    Task<IReadOnlyList<GlossaryTerm>?> GetEffectiveGlossaryByProjectCodeAsync(string projectCode, CancellationToken ct = default);

    /// <summary>
    /// 获取某个作用域下「原始」术语（不合并）：projectId 为 null 返回全局术语，否则仅返回该项目自己的术语。用于术语库管理页。
    /// </summary>
    Task<IReadOnlyList<GlossaryTerm>> GetGlossaryTermsByScopeAsync(Guid? projectId, CancellationToken ct = default);
}
