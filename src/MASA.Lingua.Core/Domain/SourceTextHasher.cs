using System.Security.Cryptography;
using System.Text;

namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 源文案哈希：每条译文记录它是基于哪一版源文案翻译/确认的，源文案变更后即可精确判断译文是否过时。
/// </summary>
public static class SourceTextHasher
{
    public static string Compute(string? sourceText)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceText ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }
}
