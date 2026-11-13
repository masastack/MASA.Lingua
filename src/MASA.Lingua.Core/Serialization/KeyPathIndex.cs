using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Serialization;

/// <summary>
/// 键路径索引：增量判断「再加入一条词条会不会与已有词条产生键路径冲突」。
/// 判定口径与 <see cref="JsonBundleConverter.DetectPathConflicts"/> 完全一致（共用 <see cref="JsonBundleConverter.ToPathSegments"/>），
/// 区别只是它能逐条增量判断 —— 用于写入口（缺词上报 / 手工新增词条）在落库之前拦截或提示冲突，
/// 而不是等到发布门禁才发现。
/// </summary>
public sealed class KeyPathIndex
{
    // 词条身份与路径各段都忽略大小写（LinguaKeys）：仅大小写不同的 (命名空间, Key) 是同一个词条，
    // 仅大小写不同的路径是同一个位置。
    private readonly HashSet<(string Namespace, string Key)> _seen = new(LinguaKeys.EntryComparer);
    private readonly Dictionary<string, List<(string Namespace, string Key)>> _values = new(LinguaKeys.Comparer);
    private readonly Dictionary<string, List<(string Namespace, string Key)>> _branches = new(LinguaKeys.Comparer);

    public KeyPathIndex()
    {
    }

    public KeyPathIndex(IEnumerable<(string Namespace, string Key)> existing)
    {
        foreach (var (ns, key) in existing)
        {
            Add(ns, key);
        }
    }

    /// <summary>登记一条词条；同 (命名空间, Key) 重复登记无影响（共享层被项目层覆盖等场景）。</summary>
    public void Add(string? namespaceCode, string? key)
    {
        var entry = (namespaceCode ?? string.Empty, key ?? string.Empty);
        var segments = JsonBundleConverter.ToPathSegments(entry.Item1, entry.Item2);
        if (segments.Length == 0 || !_seen.Add(entry))
        {
            return;
        }

        var path = string.Empty;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            path = path.Length == 0 ? segments[i] : $"{path}.{segments[i]}";
            Claim(_branches, path, entry);
        }

        Claim(_values, string.Join('.', segments), entry);
    }

    /// <summary>
    /// 若把 (namespaceCode, key) 加入当前集合会产生冲突，返回冲突描述（claims 里已含候选词条本身）；没有冲突返回 null。
    /// 候选词条与已登记的某条是同一个 (命名空间, Key)（忽略大小写）时视为同一词条（更新），不算冲突。只检查，不登记。
    /// </summary>
    public JsonBundleConverter.KeyPathConflict? FindConflict(string? namespaceCode, string? key)
    {
        var candidate = (namespaceCode ?? string.Empty, key ?? string.Empty);
        var segments = JsonBundleConverter.ToPathSegments(candidate.Item1, candidate.Item2);
        if (segments.Length == 0 || _seen.Contains(candidate))
        {
            return null;
        }

        // 1) 候选词条自己的位置：已被其他词条当作「值」占用（完全重复），或已被当作「父级对象」（词条值会把子树挤掉）
        var fullPath = string.Join('.', segments);
        _values.TryGetValue(fullPath, out var sameValues);
        _branches.TryGetValue(fullPath, out var children);
        if (sameValues is { Count: > 0 } || children is { Count: > 0 })
        {
            var valueClaims = new List<(string Namespace, string Key)>();
            if (sameValues is not null)
            {
                valueClaims.AddRange(sameValues);
            }

            valueClaims.Add(candidate);
            return new JsonBundleConverter.KeyPathConflict(
                fullPath,
                valueClaims,
                children ?? (IReadOnlyList<(string Namespace, string Key)>)[]);
        }

        // 2) 候选词条要求的某一级「父级对象」位置，已被其他词条当作「值」占用
        var path = string.Empty;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            path = path.Length == 0 ? segments[i] : $"{path}.{segments[i]}";
            if (_values.TryGetValue(path, out var blocking) && blocking.Count > 0)
            {
                return new JsonBundleConverter.KeyPathConflict(path, blocking, [candidate]);
            }
        }

        return null;
    }

    private static void Claim(
        Dictionary<string, List<(string Namespace, string Key)>> map,
        string path,
        (string Namespace, string Key) entry)
    {
        if (!map.TryGetValue(path, out var list))
        {
            map[path] = list = [];
        }

        list.Add(entry);
    }
}
