using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Abstractions;

/// <summary>
/// 项目元数据概览（不含词条/译文/术语）：管理页只需要语种、发布策略与最近发布结果的场景用它，
/// 避免为读几个字段把整个项目的词条树拉进内存。
/// </summary>
public sealed record ProjectOverview(
    string Code,
    string DefaultCulture,
    IReadOnlyList<string> SupportedCultures,
    PublishPolicy PublishPolicy,
    DateTimeOffset? LastPublishedAt,
    string? LastPublishedVersionHash);

/// <summary>
/// 项目命名空间的轻量投影：只带编码与「覆盖了共享词条」的词条 Key 集合，不含源文案与译文。
/// 供共享词条「影响面」统计（哪些项目继承、哪些已覆盖）这类只看 Key 是否存在的计算使用。
/// Key 集合用哈希集合承载：影响面统计对每个共享词条都要做一次成员判断，线性查找会随词条数平方放大。
/// 集合必须用 <see cref="Domain.LinguaKeys.Comparer"/>（忽略大小写）构建，成员判断才与词条「Key 忽略大小写」的口径一致。
/// </summary>
public sealed record ImpactNamespace(string Code, bool IsShared, IReadOnlySet<string> EntryKeys);

/// <summary>项目的影响面投影：<see cref="ImpactNamespace"/> 的项目级集合。</summary>
public sealed record ProjectImpact(string Code, IReadOnlyList<string> SharedNamespaceCodes, IReadOnlyList<ImpactNamespace> Namespaces);

/// <summary>缺词上报鉴权所需的最小项目信息（不含词条树）。</summary>
public sealed record ProjectWriteAccess(string Code, string DefaultCulture, string? WriteKey);

/// <summary>
/// 「按需创建」的结果：<see cref="Created"/> 为 true 才表示**本次调用**真正插入了这一行；
/// 项目早已存在、或并发创建时输给了另一个调用方，都是 false（此时 <see cref="Project"/> 是已有项目，不是本调用方创建的）。
/// 只有真正创建者才应该拿到生成的写密钥。
/// </summary>
public sealed record ProjectCreationResult(Project Project, bool Created);

/// <summary>
/// 发布 / 导出的数据源：项目词条树 + 项目引用的共享命名空间，来自同一次读库（同一时间点的一致视图）。
/// 都是只读的未跟踪实体，门禁检查与逐语种图层构建共用同一份，不会出现「检查时一份、编译时另一份」。
/// </summary>
public sealed record PublishSource(Project Project, IReadOnlyList<TranslationNamespace> SharedNamespaces);

/// <summary>
/// 项目主体：项目清单 / 轻量摘要 / 共享命名空间，以及**项目级配置**的读写（语种配置、发布策略）。
/// <para>
/// 切分理由：分发链路（bundle 校验、启动恢复）只需要「项目是什么」，
/// 不该顺带看到词条、术语与发布快照。两个配置写入虽然改变状态，但改的都是 <see cref="Project"/>
/// 自身的字段（不碰词条内容），与读方法同属一个主体，因此放在一起而不是另开写接口。
/// </para>
/// <para>
/// 读方法返回的对象图归调用方所有：实现可以在内部缓存，但必须返回克隆，调用方改动不会影响其他调用方。
/// </para>
/// </summary>
public interface IProjectCatalog
{
    Task<IReadOnlyList<Project>> GetAllProjectsAsync(CancellationToken ct = default);

    /// <summary>
    /// <see cref="GetAllProjectsAsync"/> 的免克隆只读版：命中缓存时直接返回缓存里的共享快照，不做整库深克隆。
    /// <para>
    /// 「只读」是硬约束：返回的对象图**归仓储缓存所有**，调用方绝不修改任何实体或集合 ——
    /// 改动会污染缓存并泄漏给其他调用方。适用「整库分析、只遍历不落笔」的场景
    /// （术语库全局作用域的命中分析：项目多、词条多时，每次进页面深克隆整库是纯粹的内存尖峰）。
    /// 需要改动时请用 <see cref="GetAllProjectsAsync"/>（克隆版，对象图归调用方所有）。
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Project>> GetAllProjectsReadOnlyAsync(CancellationToken ct = default);

    /// <summary>全部项目的元数据概览（单条投影查询，不加载命名空间/词条/译文/术语）。</summary>
    Task<IReadOnlyList<ProjectOverview>> GetProjectOverviewsAsync(CancellationToken ct = default);

    /// <summary>
    /// 全部项目的影响面投影：继承了哪些共享命名空间，以及项目自己的命名空间里与共享词条「同命名空间同 Key」的覆盖项
    /// （库内半连接，只返回重叠的 Key，不含文案与译文）。
    /// 供 <see cref="MASA.Lingua.Core.Publishing.SharedImpact"/> 做继承/覆盖统计；远轻于加载全部词条。
    /// </summary>
    Task<IReadOnlyList<ProjectImpact>> GetProjectImpactSummariesAsync(CancellationToken ct = default);

    /// <summary>单个项目的轻量摘要；项目不存在返回 null。</summary>
    Task<ProjectSummary?> GetProjectSummaryAsync(string projectCode, CancellationToken ct = default);

    /// <summary>
    /// 缺词上报鉴权用的轻量查询：只取项目编码、默认语种与写密钥，不加载词条树。项目不存在返回 null。
    /// </summary>
    Task<ProjectWriteAccess?> GetProjectWriteAccessAsync(string projectCode, CancellationToken ct = default);

    /// <summary>
    /// 按编码加载项目的词条树（命名空间 → 词条 → 译文），不含术语（术语按需用 <see cref="IGlossaryCatalog"/> 单独取）。
    /// 返回的是未跟踪的只读快照（导航属性已连好，例如 <c>entry.Namespace</c>），调用方可以就地改动自己的副本；
    /// 修改要落库请走对应的写方法。项目不存在返回 null。
    /// </summary>
    Task<Project?> GetProjectByCodeAsync(string projectCode, CancellationToken ct = default);

    Task<IReadOnlyList<TranslationNamespace>> GetSharedNamespacesAsync(CancellationToken ct = default);

    /// <summary>
    /// 一次性读取发布 / 导出所需的全部词条数据：项目词条树 + 项目引用的共享命名空间（各含词条与译文）。
    /// <para>
    /// 刻意**不走读缓存**：发布产物与导出必须基于数据库当下的状态，不能容忍多副本部署下的 TTL 陈旧窗口。
    /// 每个语种的图层用 <see cref="MASA.Lingua.Core.Compilation.TranslationLayerBuilder"/> 在内存中构建即可，
    /// 不必逐语种重新读库。项目不存在返回 null。
    /// </para>
    /// </summary>
    Task<PublishSource?> LoadPublishSourceAsync(string projectCode, CancellationToken ct = default);

    /// <summary>
    /// 更新项目的语种配置（SupportedCultures 与 DefaultCulture）。
    /// 语种代码会先规范化（en-us / zh_CN → en-US / zh-CN），只接受项目已有语种或可选语种目录（常用清单 + ICU）内的代码；
    /// 列表为空、默认语种不在列表内时抛出 <see cref="MASA.Lingua.Core.Localization.LinguaValidationException"/>。
    /// </summary>
    Task UpdateProjectCulturesAsync(
        string projectCode,
        string defaultCulture,
        IReadOnlyList<string> cultures,
        CancellationToken ct = default);

    /// <summary>保存项目发布策略（只改策略，不影响项目其他字段）。</summary>
    Task<PublishPolicy> SavePublishPolicyAsync(string projectCode, PublishPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// 按项目编码创建一份**空的多语言配置关联**：默认语种（zh-CN / zh-CN,en-US,ja-JP）+ 继承共享命名空间（common）。
    /// <para>
    /// 项目清单来自 MASA Stack PM，本地库不存项目元数据（名称、描述等一律以 PM 为准）；
    /// 该方法只为「PM 有此应用、本地还没有多语言配置」的场景补一份可编辑的配置骨架。
    /// 该编码已存在时原样返回现有项目，不重复创建。
    /// </para>
    /// <para>
    /// 编码会用作发布产物目录名，必须是安全文件名片段（LinguaProjectCodes 校验：字母 / 数字 / 连字符 / 下划线，1–64 字符），
    /// 不合法时抛 LinguaValidationException。
    /// </para>
    /// <para>创建时同步生成项目写密钥（缺词上报鉴权，见 <see cref="RegenerateWriteKeyAsync"/>）。</para>
    /// </summary>
    Task<Project> CreateProjectAsync(string projectCode, CancellationToken ct = default);

    /// <summary>
    /// <see cref="CreateProjectAsync"/> 的带结果版：同样幂等、同样的编码校验与骨架默认值，
    /// 但额外告诉调用方「这一行是不是本次调用创建的」（<see cref="ProjectCreationResult.Created"/>）。
    /// 两个调用方并发创建同一编码时，唯一索引只放行一个：赢的一方 Created = true，输的一方回读已有项目并返回 Created = false。
    /// 需要区分「新建」与「已存在」的入口（例如只向创建者返回写密钥的 HTTP 端点）必须用这个方法，
    /// 不能靠「返回了项目」推断自己创建了它。
    /// </summary>
    Task<ProjectCreationResult> CreateProjectIfAbsentAsync(string projectCode, CancellationToken ct = default);

    /// <summary>
    /// 重置项目写密钥（缺词上报鉴权，<c>X-Lingua-Key</c> 头）：生成新密钥并返回明文（旧密钥立即失效）。
    /// 项目不存在时返回 null。
    /// </summary>
    Task<string?> RegenerateWriteKeyAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// 删除项目：先清发布历史（publish_snapshots 对 projects 是 RESTRICT），再删项目本体，
    /// 命名空间 / 词条 / 译文 / 术语由外键级联清除。项目不存在返回 false。
    /// 走仓储而不是端点直连数据库，是为了让读缓存随删除一起失效。
    /// </summary>
    Task<bool> DeleteProjectAsync(string projectCode, CancellationToken ct = default);
}
