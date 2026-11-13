using System.Collections.Frozen;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Serialization;

namespace MASA.Lingua.SDK.Abstractions;

/// <summary>
/// 基于 .NET 8 FrozenDictionary 的无锁极速内存字典存储：
/// 1. 读取时 0 锁开销、纳秒级查找；
/// 2. 当本地 {culture}.json 更新或从服务端拉取到新 {culture}.json 时，构建新 FrozenDictionary 并通过 Interlocked.Exchange 原子替换引用。
/// </summary>
public sealed class LinguaFrozenMemoryStore(ILinguaChangeNotifier? notifier = null) : ILinguaMemoryStore
{
    private volatile FrozenDictionary<string, FrozenDictionary<string, string>> _cultureDictionaries =
        FrozenDictionary<string, FrozenDictionary<string, string>>.Empty;

    private readonly object _writeLock = new();

    // 回退语种：由同步服务按平台 manifest 的 defaultCulture 设置；首次同步前用 zh-CN。
    private volatile string _defaultCulture = "zh-CN";

    // 平台下发的支持语种（manifest 的 cultures）：null = 还不知道（还没同步过）
    private volatile IReadOnlyList<string>? _supportedCultures;

    public void SetDefaultCulture(string culture)
    {
        if (!string.IsNullOrWhiteSpace(culture))
        {
            _defaultCulture = culture;
        }
    }

    /// <summary>
    /// 记录平台下发的支持语种。语种集合不变时不替换引用，避免每轮对账都制造新数组。
    /// </summary>
    public void SetSupportedCultures(IReadOnlyList<string> cultures)
    {
        if (cultures is null || cultures.Count == 0)
        {
            return;
        }

        var current = _supportedCultures;
        if (current is not null
            && current.Count == cultures.Count
            && current.SequenceEqual(cultures, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _supportedCultures = cultures.ToArray();
    }

    public IReadOnlyList<string>? SupportedCultures => _supportedCultures;

    public bool TryGetValue(string culture, string key, out string value)
    {
        var snapshot = _cultureDictionaries;
        var normalizedKey = key.Replace(':', '.');

        if (TryLookupInCulture(snapshot, culture, key, normalizedKey, out value))
        {
            return true;
        }

        // Neutral culture fallback (e.g. zh-CN -> zh)
        var dashIdx = culture.IndexOf('-');
        if (dashIdx > 0)
        {
            var neutral = culture[..dashIdx];
            if (TryLookupInCulture(snapshot, neutral, key, normalizedKey, out value))
            {
                return true;
            }
        }

        if (!string.Equals(culture, _defaultCulture, StringComparison.OrdinalIgnoreCase) &&
            TryLookupInCulture(snapshot, _defaultCulture, key, normalizedKey, out value))
        {
            return true;
        }

        value = string.Empty;
        return false;
    }

    public IReadOnlyList<string> GetLoadedCultures()
    {
        // 快照不可变，直接枚举键即可；排序只为让诊断输出稳定可比
        var snapshot = _cultureDictionaries;
        var cultures = new List<string>(snapshot.Count);
        foreach (var culture in snapshot.Keys)
        {
            cultures.Add(culture);
        }

        cultures.Sort(StringComparer.OrdinalIgnoreCase);
        return cultures;
    }

    public string? DefaultCulture => _defaultCulture;

    public IEnumerable<KeyValuePair<string, string>> GetAll(string culture, string? namespacePrefix = null)
    {
        var snapshot = _cultureDictionaries;
        if (!snapshot.TryGetValue(culture, out var dict) &&
            !snapshot.TryGetValue(_defaultCulture, out dict))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(namespacePrefix))
        {
            return dict.Where(kv => !kv.Key.Contains(':'));
        }

        // 命名空间忽略大小写（LinguaKeys）：IStringLocalizer<OrderController> 的 order. 前缀也能列出库里 Order 命名空间的词条
        var dotPrefix = $"{namespacePrefix}.";
        return dict
            .Where(kv => kv.Key.StartsWith(dotPrefix, LinguaKeys.Comparison))
            .Select(kv => new KeyValuePair<string, string>(kv.Key[dotPrefix.Length..], kv.Value));
    }

    public void UpdateCultureFromJsonBytes(string culture, ReadOnlySpan<byte> utf8Json)
    {
        // 取词不区分大小写：词条 Key 与命名空间在 Lingua 里就是忽略大小写唯一的（LinguaKeys），
        // 业务代码写 Confirm / confirm 取到的是同一条
        var flattened = JsonBundleConverter.FlattenToLookupDictionary(utf8Json)
            .ToFrozenDictionary(LinguaKeys.Comparer);

        lock (_writeLock)
        {
            var current = _cultureDictionaries.ToDictionary(
                kv => kv.Key,
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);

            current[culture] = flattened;
            Interlocked.Exchange(
                ref _cultureDictionaries,
                current.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
        }

        notifier?.NotifyCultureUpdated(culture);
    }

    public void LoadFromLocalDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            // 资源目录里一个文件即一个语种：文件名必须是合法 BCP47 语种码（见 LinguaCultureFiles），
            // supportedCultures.json 等元数据 / 杂项文件名不满足形状，天然跳过；store 键用规范化后的语种名。
            if (!LinguaCultureFiles.TryResolveCultureFileName(Path.GetFileNameWithoutExtension(file), out var culture))
            {
                continue;
            }

            try
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length > 0)
                {
                    UpdateCultureFromJsonBytes(culture, bytes);
                }
            }
            catch
            {
                // Ignore malformed or locked temporary file during initial scan
            }
        }
    }

    private static bool TryLookupInCulture(
        FrozenDictionary<string, FrozenDictionary<string, string>> snapshot,
        string culture,
        string rawKey,
        string normalizedKey,
        out string value)
    {
        if (snapshot.TryGetValue(culture, out var dict))
        {
            if (dict.TryGetValue(rawKey, out var v) || dict.TryGetValue(normalizedKey, out v))
            {
                value = v;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}

public sealed class LinguaChangeNotifier : ILinguaChangeNotifier
{
    public event Action<string>? OnCultureUpdated;

    public void NotifyCultureUpdated(string culture) =>
        OnCultureUpdated?.Invoke(culture);
}
