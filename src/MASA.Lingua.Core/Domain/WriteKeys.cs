using System.Security.Cryptography;

namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 项目写密钥（缺词上报鉴权）的生成与校验。
/// <para>
/// 密钥格式：<c>lingua_sk_</c> + 32 位随机 hex（16 字节 = 128 位熵），仅在创建 / 重置时生成一次并明文展示，
/// 之后只能重置。校验走 SHA-256 + <see cref="CryptographicOperations.FixedTimeEquals"/> 防时序侧信道。
/// </para>
/// <para>
/// 密钥只保护缺词上报写端点（<c>POST /api/v1/telemetry/missing-keys</c>），且**只有服务端 SDK 会携带**——
/// MAUI / Vue 等客户端 SDK 已移除上报能力，密钥不下发到不可信客户端。
/// </para>
/// </summary>
public static class WriteKeys
{
    public const string HeaderName = "X-Lingua-Key";

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return $"lingua_sk_{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    /// <summary>常量时间比较：stored 为空时一律不匹配（未生成密钥的项目不接受上报）。</summary>
    public static bool Matches(string? stored, string? provided)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var storedHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stored));
        var providedHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(storedHash, providedHash);
    }
}
