using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Abstractions;

/// <summary>
/// 发布历史的一页（新到旧，不含产物内容）：<paramref name="TotalCount"/> 是该项目的快照总数，
/// 用于分页器算页数，不受本页 <c>take</c> 影响。
/// </summary>
public sealed record PublishSnapshotPage(IReadOnlyList<PublishSnapshot> Items, int TotalCount);

/// <summary>
/// 发布归档：每次发布 / 回滚产生的快照。写入只有「追加一次快照」，
/// 读取是发布历史、回滚目标与启动恢复（最近一次带内容的快照）。
/// <para>
/// 切分理由：快照是「已上线内容」的唯一权威来源，写入方只有发布器，读取方是发布历史页与启动恢复；
/// 把它与词条、项目配置分开，可以让发布器明确「我改的是归档，不是草稿」。
/// 保留策略由实现负责：只保留最近若干次快照的内容，更早的记录仍可查但无法回滚。
/// </para>
/// </summary>
public interface IPublishArchive
{
    /// <summary>记录发布快照（含各语种产物内容），并只保留最近若干次快照的内容。</summary>
    Task RecordPublishSnapshotAsync(PublishSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// 发布历史按页取（新到旧，不含产物内容），同时返回总条数。
    /// 排序在 <c>PublishedAt</c> 之外再按 Id 兜底：同一次操作里写入的多条快照时间可能相同，
    /// 不稳定排序会让翻页时漏行或重复。
    /// </summary>
    Task<PublishSnapshotPage> GetPublishSnapshotPageAsync(
        string projectCode, int skip = 0, int take = 20, CancellationToken ct = default);

    /// <summary>最近若干条发布历史（新到旧，不含产物内容）：<see cref="GetPublishSnapshotPageAsync"/> 的首屏便捷写法。</summary>
    Task<IReadOnlyList<PublishSnapshot>> GetPublishSnapshotsAsync(string projectCode, int take = 30, CancellationToken ct = default);

    /// <summary>读取指定快照（含产物内容），用于回滚。</summary>
    Task<PublishSnapshot?> GetPublishSnapshotAsync(string projectCode, Guid snapshotId, CancellationToken ct = default);

    /// <summary>最近一次保存了内容的快照（含产物内容），用于启动时恢复线上版本。</summary>
    Task<PublishSnapshot?> GetLatestPublishSnapshotWithContentAsync(string projectCode, CancellationToken ct = default);
}
