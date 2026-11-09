using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MASA.Lingua.Core.Publishing;

/// <summary>
/// 发布快照内容的打包 / 解包：gzip( JSON { "zh-CN": "<嵌套 JSON 文本>", ... } )。
/// 嵌套 JSON 以字符串原样保存，解包得到的字节与发布时逐字节一致，因此回滚后 ETag 与原版本相同。
/// </summary>
public static class SnapshotContentCodec
{
    public static byte[] Pack(IEnumerable<(string Culture, ReadOnlyMemory<byte> NestedJson)> bundles)
    {
        var map = bundles.ToDictionary(
            b => b.Culture,
            b => Encoding.UTF8.GetString(b.NestedJson.Span),
            StringComparer.Ordinal);

        var json = JsonSerializer.SerializeToUtf8Bytes(map);
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(json);
        }

        return ms.ToArray();
    }

    public static IReadOnlyDictionary<string, byte[]> Unpack(byte[] content)
    {
        using var input = new MemoryStream(content);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(gzip)
                  ?? throw new InvalidDataException("快照内容为空。");

        return map.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value), StringComparer.Ordinal);
    }
}
