namespace MASA.Lingua.Server.Startup;

/// <summary>
/// 登录 <c>returnUrl</c> 净化：只放行站内相对路径，其余一律回落 <c>/</c>。
/// <para>
/// 判定必须在<b>反解码之后</b>做：<c>%2F%2Fevil.com</c> 解码后是 <c>//evil.com</c>，
/// 只检查原始串会被绕过。反斜杠一律拒绝 —— 部分浏览器把 <c>\</c> 归一化成 <c>/</c>，
/// 于是 <c>/\evil.com</c> 等价于协议相对地址 <c>//evil.com</c>。
/// </para>
/// </summary>
public static class ReturnUrlSanitizer
{
    public const string Fallback = "/";

    public static string Sanitize(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return Fallback;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(returnUrl);
        }
        catch (UriFormatException)
        {
            return Fallback;
        }

        if (decoded.Length == 0 || decoded[0] != '/')
        {
            return Fallback;
        }

        // 协议相对地址与反斜杠变体、控制字符（可被用于拆分/归一化绕过）
        if (decoded.StartsWith("//", StringComparison.Ordinal))
        {
            return Fallback;
        }

        foreach (var ch in decoded)
        {
            if (ch == '\\' || char.IsControl(ch))
            {
                return Fallback;
            }
        }

        return decoded;
    }
}
