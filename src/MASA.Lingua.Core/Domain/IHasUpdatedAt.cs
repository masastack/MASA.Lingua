namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 带「最后更新时间」的实体：时间戳由持久化层在 SaveChanges 时统一填充，
/// 业务代码不再手工赋值（避免各处时钟不一致、遗漏更新）。
/// </summary>
public interface IHasUpdatedAt
{
    DateTimeOffset UpdatedAt { get; set; }
}
