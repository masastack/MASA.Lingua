using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MASA.Lingua.Core.Publishing;

/// <summary>一个语种在快照里保存的已发布字节。<see cref="Flat"/> 为 null 表示旧版快照（只保存了嵌套 JSON）。</summary>
public sealed record SnapshotBundleBytes(byte[] Nested, byte[]? Flat);

/// <summary>
/// 发布快照内容的打包 / 解包：gzip( JSON )，嵌套 / 扁平 JSON 都以字符串原样保存，
/// 解包得到的字节与发布时逐字节一致，因此回滚 / 重启恢复后两种表示的内容与 ETag 都与原版本相同。
/// <list type="bullet">
/// <item>v2（当前）：<c>{ "$format": 2, "bundles": { "zh-CN": { "nested": "…", "flat": "…" } } }</c>。
/// 必须同时保存扁平产物 —— 嵌套 JSON 无法表达键路径冲突里被挤掉的一侧，.NET 客户端消费的恰恰是扁平文件，
/// 只存嵌套会让恢复出来的线上版本与发布时不一致（丢词条、ETag 变化）。</item>
/// <item>v1（旧版，仍可读取）：<c>{ "zh-CN": "&lt;嵌套 JSON 文本&gt;" }</c>，没有扁平产物。</item>
/// </list>
/// </summary>
public static class SnapshotContentCodec
{
    private const string FormatProperty = "$format";
    private const string BundlesProperty = "bundles";
    private const int CurrentFormat = 2;

    /// <summary>以当前格式（v2）打包：同时保存每个语种的嵌套 + 扁平产物。</summary>
    public static byte[] Pack(
        IEnumerable<(string Culture, ReadOnlyMemory<byte> NestedJson, ReadOnlyMemory<byte> FlatJson)> bundles)
    {
        using var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WriteNumber(FormatProperty, CurrentFormat);
            writer.WriteStartObject(BundlesProperty);
            foreach (var (culture, nested, flat) in bundles)
            {
                writer.WriteStartObject(culture);
                writer.WriteString("nested", Encoding.UTF8.GetString(nested.Span));
                writer.WriteString("flat", Encoding.UTF8.GetString(flat.Span));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Compress(json.ToArray());
    }

    /// <summary>以旧版（v1）格式打包，只保存嵌套 JSON。仅为兼容保留；发布流程请使用带扁平产物的重载。</summary>
    public static byte[] Pack(IEnumerable<(string Culture, ReadOnlyMemory<byte> NestedJson)> bundles)
    {
        var map = bundles.ToDictionary(
            b => b.Culture,
            b => Encoding.UTF8.GetString(b.NestedJson.Span),
            StringComparer.Ordinal);

        return Compress(JsonSerializer.SerializeToUtf8Bytes(map));
    }

    /// <summary>解包为每个语种的已发布字节；v1 快照的 <see cref="SnapshotBundleBytes.Flat"/> 为 null。</summary>
    public static IReadOnlyDictionary<string, SnapshotBundleBytes> UnpackBundles(byte[] content)
    {
        using var input = new MemoryStream(content);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gzip);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("快照内容为空。");
        }

        var result = new Dictionary<string, SnapshotBundleBytes>(StringComparer.Ordinal);
        if (root.TryGetProperty(FormatProperty, out var format) && format.ValueKind == JsonValueKind.Number)
        {
            if (!root.TryGetProperty(BundlesProperty, out var bundles) || bundles.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("快照内容缺少 bundles。");
            }

            foreach (var culture in bundles.EnumerateObject())
            {
                var nested = culture.Value.GetProperty("nested").GetString()
                             ?? throw new InvalidDataException($"快照缺少 {culture.Name} 的嵌套产物。");
                var flat = culture.Value.TryGetProperty("flat", out var flatText) && flatText.ValueKind == JsonValueKind.String
                    ? Encoding.UTF8.GetBytes(flatText.GetString()!)
                    : null;
                result[culture.Name] = new SnapshotBundleBytes(Encoding.UTF8.GetBytes(nested), flat);
            }

            return result;
        }

        foreach (var culture in root.EnumerateObject())
        {
            result[culture.Name] = new SnapshotBundleBytes(Encoding.UTF8.GetBytes(culture.Value.GetString() ?? string.Empty), null);
        }

        return result;
    }

    /// <summary>仅取每个语种的嵌套字节（兼容旧调用方）；需要扁平产物请用 <see cref="UnpackBundles"/>。</summary>
    public static IReadOnlyDictionary<string, byte[]> Unpack(byte[] content) =>
        UnpackBundles(content).ToDictionary(kv => kv.Key, kv => kv.Value.Nested, StringComparer.Ordinal);

    private static byte[] Compress(byte[] json)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(json);
        }

        return ms.ToArray();
    }
}
