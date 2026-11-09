using System.Collections.Frozen;
using System.Security.Cryptography;
using MASA.Lingua.Core.Serialization;

namespace MASA.Lingua.Core.Compilation;

public sealed record TranslationItem(string Namespace, string Key, string Value);

public sealed record TranslationLayer(string LayerName, IReadOnlyList<TranslationItem> Items);

public sealed record CompiledBundle(
    string ProjectCode,
    string Culture,
    string ETag,
    string ContentHash,
    ReadOnlyMemory<byte> NestedJsonUtf8Bytes,
    ReadOnlyMemory<byte> FlatJsonUtf8Bytes,
    FrozenDictionary<string, string> LookupEntries);

/// <summary>
/// 多语言包预编译器：
/// 负责按优先级合并 [公共命名空间回退语种 -> 公共命名空间目标语种 -> 项目回退语种 -> 项目目标语种]，
/// 产出标准嵌套/扁平 {culture}.json 字节、SHA256 确定性 ETag 及进程内只读 FrozenDictionary。
/// </summary>
public sealed class BundleCompiler
{
    public CompiledBundle Compile(
        string projectCode,
        string culture,
        IEnumerable<TranslationLayer> orderedLayers)
    {
        var merged = new Dictionary<(string Namespace, string Key), string>();

        foreach (var layer in orderedLayers)
        {
            foreach (var item in layer.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Value))
                {
                    merged[(item.Namespace, item.Key)] = item.Value;
                }
            }
        }

        var tuples = merged
            .Select(kv => (kv.Key.Namespace, kv.Key.Key, kv.Value))
            .OrderBy(t => t.Namespace, StringComparer.Ordinal)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .ToList();

        byte[] nestedBytes = JsonBundleConverter.ToNestedJsonUtf8Bytes(tuples);
        return CreateBundle(projectCode, culture, nestedBytes, tuples);
    }

    /// <summary>
    /// 由「已发布」的嵌套 JSON 字节还原编译产物（服务重启后从快照/磁盘恢复线上版本，字节与 ETag 与发布时完全一致）。
    /// </summary>
    public CompiledBundle FromPublishedBytes(string projectCode, string culture, byte[] nestedJsonUtf8Bytes)
    {
        var tuples = JsonBundleConverter.ParseExistingJson(nestedJsonUtf8Bytes, "")
            .Select(t => (t.Namespace, t.Key, t.Value))
            .ToList();
        return CreateBundle(projectCode, culture, nestedJsonUtf8Bytes, tuples);
    }

    private static CompiledBundle CreateBundle(
        string projectCode,
        string culture,
        byte[] nestedBytes,
        IReadOnlyList<(string Namespace, string Key, string Value)> tuples)
    {
        byte[] flatBytes = JsonBundleConverter.ToFlatJsonUtf8Bytes(tuples, '.');
        string hash = Convert.ToHexString(SHA256.HashData(nestedBytes)).ToLowerInvariant()[..16];
        string etag = $"\"lingua-{hash}\"";

        var lookup = JsonBundleConverter.FlattenToLookupDictionary(nestedBytes)
            .ToFrozenDictionary(StringComparer.Ordinal);

        return new CompiledBundle(
            projectCode,
            culture,
            etag,
            hash,
            nestedBytes,
            flatBytes,
            lookup);
    }

    /// <summary>
    /// 项目级版本指纹：对所有语种的内容哈希整体取 SHA256（与语种顺序无关），任何一个语种变化指纹都会变化。
    /// </summary>
    public static string ComputeVersionHash(IEnumerable<(string Culture, string ContentHash)> bundles)
    {
        var ordered = bundles
            .OrderBy(b => b.Culture, StringComparer.OrdinalIgnoreCase)
            .Select(b => $"{b.Culture.ToLowerInvariant()}:{b.ContentHash}")
            .ToList();

        if (ordered.Count == 0)
        {
            return "initial";
        }

        var payload = string.Join('|', ordered);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()[..16];
    }

    /// <summary>
    /// 计算语言回退链（例如 "zh-CN" -> ["zh-CN", "zh", defaultCulture]）
    /// </summary>
    public static IReadOnlyList<string> BuildFallbackChain(string targetCulture, string defaultCulture)
    {
        var chain = new List<string>();

        if (!string.IsNullOrWhiteSpace(defaultCulture))
        {
            chain.Add(defaultCulture);
        }

        var dashIndex = targetCulture.IndexOf('-');
        if (dashIndex > 0)
        {
            var neutral = targetCulture[..dashIndex];
            if (!chain.Contains(neutral, StringComparer.OrdinalIgnoreCase))
            {
                chain.Add(neutral);
            }
        }

        if (!chain.Contains(targetCulture, StringComparer.OrdinalIgnoreCase))
        {
            chain.Add(targetCulture);
        }
        else
        {
            // 确保目标语言排在最后（优先级最高）
            chain.RemoveAll(c => string.Equals(c, targetCulture, StringComparison.OrdinalIgnoreCase));
            chain.Add(targetCulture);
        }

        return chain;
    }
}
