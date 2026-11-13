using MASA.Lingua.Core.Serialization;

namespace MASA.Lingua.Core.Compilation;

/// <summary>两份语言包之间的差异：新增 / 修改 / 删除了多少个 key。</summary>
public sealed record BundleDiffResult(int Added, int Changed, int Removed, IReadOnlySet<string> ChangedKeys)
{
    public int Total => Added + Changed + Removed;
}

public static class BundleDiff
{
    /// <param name="published">线上已发布的 JSON；"hasPublished" 为 false 表示还没发布过（草稿全部算新增）。</param>
    /// <param name="draft">草稿编译出的 JSON。两份必须是同一种表示；推荐传扁平 JSON —— 嵌套 JSON 无法表达键路径冲突里被挤掉的一侧，
    /// 只改这一侧时嵌套字节不变，差异会漏报，而 .NET 客户端消费的恰恰是扁平文件。</param>
    public static BundleDiffResult Compare(ReadOnlySpan<byte> published, ReadOnlySpan<byte> draft, bool hasPublished = true)
    {
        var draftMap = ToMap(draft);
        var publishedMap = hasPublished ? ToMap(published) : new Dictionary<string, string>();

        int added = 0, changed = 0, removed = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, value) in draftMap)
        {
            if (!publishedMap.TryGetValue(key, out var old))
            {
                added++;
                keys.Add(key);
            }
            else if (!string.Equals(old, value, StringComparison.Ordinal))
            {
                changed++;
                keys.Add(key);
            }
        }

        foreach (var key in publishedMap.Keys)
        {
            if (!draftMap.ContainsKey(key))
            {
                removed++;
                keys.Add(key);
            }
        }

        return new BundleDiffResult(added, changed, removed, keys);
    }

    private static Dictionary<string, string> ToMap(ReadOnlySpan<byte> json)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (ns, key, value) in JsonBundleConverter.ParseExistingJson(json, ""))
        {
            map[string.IsNullOrEmpty(ns) ? key : $"{ns}.{key}"] = value;
        }

        return map;
    }
}
