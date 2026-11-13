using System.Diagnostics.CodeAnalysis;
using MASA.Lingua.Core.Localization;

namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 项目编码（= MASA Stack PM 中应用的 Identity）的安全校验。
/// <para>
/// 编码会作为目录名参与服务端发布产物的磁盘路径（<c>App_Data/i18n/{code}</c>），也出现在公开的分发 URL 中，
/// 因此必须限定为「安全文件名片段」：ASCII 字母 / 数字 / 连字符 / 下划线——拒绝路径分隔符（<c>/</c>、<c>\</c>）、
/// 父目录片段（<c>..</c>）与空白、控制字符，防止匿名创建项目后把发布产物写到 <c>i18n</c> 目录之外（路径穿越）。
/// </para>
/// <para>
/// 校验在 HTTP 端点层与仓储层统一执行（<c>CreateProjectAsync</c>），发布器写盘 / 读盘前再做一次兜底——
/// 兜底防的是校验上线前已经入库的非法编码项目。
/// </para>
/// <para>
/// <see cref="IsValid"/> <b>不做 Trim</b>：编码会原样进磁盘路径与缓存键，「校验通过」必须与「实际使用」是同一个字符串，
/// 带首尾空白的编码按非法处理。接收外部输入的入口应先用 <see cref="EnsureValid"/>（或自行 Trim）取得规范化编码，再往下传。
/// </para>
/// </summary>
public static class LinguaProjectCodes
{
    /// <summary>编码长度上限，与 projects.code 列宽一致（见 Storage.EfCore 的 ProjectConfiguration）。</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// 是否为安全的项目编码（逐字符校验、不 Trim）。合法形状即 PM Identity 的实际形状：
    /// <c>auth-web-staging</c>、<c>Lonsid_MESPro_Web_Host</c>。
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? projectCode) =>
        !string.IsNullOrEmpty(projectCode)
        && projectCode.Length <= MaxLength
        && projectCode.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    /// <summary>校验并返回规范化（Trim）后的编码；不合法时抛 <see cref="LinguaValidationException"/>。</summary>
    public static string EnsureValid(string? projectCode)
    {
        var code = projectCode?.Trim();
        if (!IsValid(code))
        {
            throw new LinguaValidationException(
                $"项目编码不合法：「{Describe(projectCode)}」。只允许字母、数字、连字符（-）与下划线（_），长度 1–{MaxLength}，"
                + "且不能包含路径分隔符或「..」（编码会用作发布产物目录名）。");
        }

        return code;
    }

    /// <summary>错误信息里回显的编码：外部输入可能很长或带控制字符，截断并替换掉控制字符，避免污染日志与界面。</summary>
    private static string Describe(string? projectCode)
    {
        if (string.IsNullOrEmpty(projectCode))
        {
            return string.Empty;
        }

        var shown = projectCode.Length > 80 ? projectCode[..80] + "…" : projectCode;
        return new string(shown.Select(ch => char.IsControl(ch) ? '?' : ch).ToArray());
    }
}
