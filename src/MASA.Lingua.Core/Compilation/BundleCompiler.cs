using System.Collections.Frozen;
using System.Security.Cryptography;
using MASA.Lingua.Core.Domain;
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
    FrozenDictionary<string, string> LookupEntries,
    int EntryCount);

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
        // 图层按优先级由低到高叠加，同一词条（命名空间 / Key 忽略大小写）后者覆盖前者；
        // 结果沿用最高优先级图层里的写法（先移除再写入，字典不会保留被覆盖项的旧写法）。
        var merged = new Dictionary<(string Namespace, string Key), string>(LinguaKeys.EntryComparer);

        foreach (var layer in orderedLayers)
        {
            foreach (var item in layer.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Value))
                {
                    var identity = (item.Namespace, item.Key);
                    merged.Remove(identity);
                    merged[identity] = item.Value;
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
    /// 由「已发布」的嵌套 + 扁平两种 JSON 字节原样还原编译产物（服务重启后从快照/磁盘恢复线上版本，
    /// 两种表示的字节与 ETag 都与发布时逐字节一致）。
    /// <para>
    /// 必须两种字节一起还原：嵌套 JSON 无法表达键路径冲突（根裸 key 与同名命名空间共存、点分 key 与「命名空间 + key」
    /// 拼出同一路径）里被挤掉的一侧，只拿嵌套字节去重建扁平，会让 .NET 客户端（只下载扁平）取词时静默丢掉那一侧，
    /// 且 ETag 与发布时不同。
    /// </para>
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">扁平字节不是合法 JSON（磁盘文件损坏）。</exception>
    public CompiledBundle FromPublishedBytes(
        string projectCode,
        string culture,
        byte[] nestedJsonUtf8Bytes,
        byte[] flatJsonUtf8Bytes)
    {
        return CreateBundle(projectCode, culture, nestedJsonUtf8Bytes, flatJsonUtf8Bytes);
    }

    /// <summary>
    /// 只有嵌套字节时的还原（旧版快照没有保存扁平产物）：扁平表示由嵌套内容重建，
    /// 无键路径冲突的项目与发布时完全一致；存在冲突的项目，被嵌套挤掉的一侧无法从嵌套字节找回，
    /// 调用方应优先使用 <see cref="FromPublishedBytes(string, string, byte[], byte[])"/> 或按草稿重编译。
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
        IReadOnlyList<(string Namespace, string Key, string Value)> tuples) =>
        CreateBundle(projectCode, culture, nestedBytes, JsonBundleConverter.ToFlatJsonUtf8Bytes(tuples, '.'));

    private static CompiledBundle CreateBundle(
        string projectCode,
        string culture,
        byte[] nestedBytes,
        byte[] flatBytes)
    {
        string nestedHash = Convert.ToHexString(SHA256.HashData(nestedBytes)).ToLowerInvariant()[..16];
        string flatHash = Convert.ToHexString(SHA256.HashData(flatBytes)).ToLowerInvariant()[..16];

        // 内容指纹必须同时覆盖两种对外表示：嵌套 JSON 无法表达「裸 key 与同名命名空间共存」（键路径冲突），
        // 这种词条的文案只在扁平文件里出现——只算嵌套字节时，改了译值指纹与 ETag 依然纹丝不动，
        // 客户端（按 ETag / 版本对账）就永远收不到更新。两种字节一起取哈希，任一表示变化指纹都变化。
        string contentHash = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{nestedHash}:{flatHash}")))
            .ToLowerInvariant()[..16];
        string etag = $"\"lingua-{contentHash}\"";

        var lookup = JsonBundleConverter.FlattenToLookupDictionary(nestedBytes)
            .ToFrozenDictionary(LinguaKeys.Comparer);

        return new CompiledBundle(
            projectCode,
            culture,
            etag,
            contentHash,
            nestedBytes,
            flatBytes,
            lookup,
            CountFlatEntries(flatBytes));
    }

    /// <summary>
    /// 扁平发布文件的词条数 = 客户端实际能取到的词条条数（每个顶层属性一条）。
    /// 不能用 <see cref="CompiledBundle.LookupEntries"/> 的条数：它为每条词条存了点 / 冒号 / 混合三种别名键，数量是词条数的数倍。
    /// 扁平字节不是合法 JSON 对象时抛出 <see cref="System.Text.Json.JsonException"/>（磁盘文件损坏）。
    /// </summary>
    private static int CountFlatEntries(byte[] flatBytes)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(flatBytes);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new System.Text.Json.JsonException("扁平多语言文件的根节点必须是 JSON 对象。");
        }

        var count = 0;
        foreach (var _ in doc.RootElement.EnumerateObject())
        {
            count++;
        }

        return count;
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
