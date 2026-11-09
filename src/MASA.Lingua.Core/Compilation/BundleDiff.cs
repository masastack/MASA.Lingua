using MASA.Lingua.Core.Serialization;

namespace MASA.Lingua.Core.Compilation;

/// <summary>两份语言包（嵌套 JSON）之间的差异：新增 / 修改 / 删除了多少个 key。</summary>
public sealed record BundleDiffResult(int Added, int Changed, int Removed, IReadOnlySet<string> ChangedKeys)
{
    public int Total => Added + Changed + Removed;
}

public static class BundleDiff
{
    /// <param name="published">线上已发布的嵌套 JSON；null 表示还没发布过（草稿全部算新增）。</param>
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

    private static Dictionary<string, string> ToMap(ReadOnlySpan<byte> nestedJson)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (ns, key, value) in JsonBundleConverter.ParseExistingJson(nestedJson, ""))
        {
            map[$"{ns}.{key}"] = value;
        }

        return map;
    }
}
