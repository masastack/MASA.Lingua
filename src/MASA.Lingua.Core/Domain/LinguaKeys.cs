namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 命名空间编码与词条 Key 的统一比较口径：<b>忽略大小写（<see cref="StringComparison.OrdinalIgnoreCase"/>）</b>。
/// <para>
/// 为什么忽略大小写：仅大小写不同的 Key（<c>Confirm</c> / <c>confirm</c>）几乎总是手误，
/// 而消费方对它们的处理并不一致（MasaBlazor 的 I18n 缓存忽略大小写，会把它们合并；区分大小写的客户端则当成两个词条）。
/// 与其让「同一个词条在不同客户端里时有时无」，不如在源头就规定它们是同一个词条：
/// 存储唯一性、写入查找、发布合并、冲突检测与各 SDK 的取词都使用这同一个口径。
/// </para>
/// <para>
/// 存储的是词条<b>首次创建时的写法</b>（发布产物里的大小写与之一致）；之后用不同大小写写入同一词条是「更新」，不会改写它。
/// 数据库侧由 <c>lower(key)</c> / <c>lower(code)</c> 表达式唯一索引兜底，应用侧用本类的比较器与 <see cref="Fold"/>。
/// </para>
/// </summary>
public static class LinguaKeys
{
    /// <summary>命名空间编码 / Key 的字符串比较器（忽略大小写，序号比较，与区域设置无关）。</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>与 <see cref="Comparer"/> 对应的 <see cref="StringComparison"/>。</summary>
    public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>(命名空间, Key) 对的相等比较器：两段都忽略大小写。</summary>
    public static IEqualityComparer<(string Namespace, string Key)> EntryComparer { get; } = new EntryIdentityComparer();

    /// <summary>
    /// 折叠成比较用的规范写法（不变区域小写）。拼进数据库查询时用它（<c>e.Key.ToLower() == LinguaKeys.Fold(key)</c>，
    /// 落到 <c>lower(key)</c> 表达式索引上）；不要用于展示或存储。
    /// </summary>
    public static string Fold(string? value) => string.IsNullOrEmpty(value) ? string.Empty : value.ToLowerInvariant();

    /// <summary>折叠一组值（去重）。</summary>
    public static List<string> FoldAll(IEnumerable<string?> values) =>
        values.Select(Fold).Distinct(StringComparer.Ordinal).ToList();

    private sealed class EntryIdentityComparer : IEqualityComparer<(string Namespace, string Key)>
    {
        public bool Equals((string Namespace, string Key) x, (string Namespace, string Key) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Namespace ?? string.Empty, y.Namespace ?? string.Empty)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Key ?? string.Empty, y.Key ?? string.Empty);

        public int GetHashCode((string Namespace, string Key) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Namespace ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key ?? string.Empty));
    }
}
