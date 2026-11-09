namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 命名空间（模块级隔离，如 common, auth, order）。
/// <see cref="ProjectId"/> 为 null 表示跨项目共享的全局命名空间。
/// </summary>
public sealed class TranslationNamespace
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>所属项目；null 表示全局共享命名空间。</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>命名空间编码，项目范围内唯一（全局范围内也唯一）。</summary>
    public required string Code { get; set; }

    public bool IsShared { get; set; }

    public List<TranslationEntry> Entries { get; set; } = [];
}
