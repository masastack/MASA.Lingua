using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.SDK.Abstractions;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>项目编码 / 语种名的安全校验：它们会原样拼进磁盘路径，必须挡住路径穿越与不可见字符。</summary>
public sealed class LinguaProjectCodesTests
{
    [Theory]
    [InlineData("shop-vue")]
    [InlineData("auth-web-staging")]
    [InlineData("Lonsid_MESPro_Web_Host")]
    [InlineData("a")]
    [InlineData("0")]
    public void IsValid_Should_Accept_Pm_Identity_Shapes(string code)
    {
        Assert.True(LinguaProjectCodes.IsValid(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" shop")]            // 不做 Trim：校验通过的必须就是实际使用的字符串
    [InlineData("shop ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a.b")]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("项目")]              // 仅限 ASCII
    [InlineData("ｓｈｏｐ")]          // 全角字母不是 ASCII
    public void IsValid_Should_Reject_Unsafe_Codes(string? code)
    {
        Assert.False(LinguaProjectCodes.IsValid(code));
    }

    [Fact]
    public void IsValid_Should_Enforce_The_Length_Limit()
    {
        Assert.True(LinguaProjectCodes.IsValid(new string('a', LinguaProjectCodes.MaxLength)));
        Assert.False(LinguaProjectCodes.IsValid(new string('a', LinguaProjectCodes.MaxLength + 1)));
    }

    [Fact]
    public void EnsureValid_Should_Trim_And_Return_The_Normalized_Code()
    {
        Assert.Equal("shop-vue", LinguaProjectCodes.EnsureValid("  shop-vue\t"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../evil")]
    [InlineData("a b")]
    public void EnsureValid_Should_Throw_A_Validation_Exception_For_Unsafe_Codes(string? code)
    {
        Assert.Throws<LinguaValidationException>(() => LinguaProjectCodes.EnsureValid(code));
    }

    [Fact]
    public void EnsureValid_Message_Should_Not_Echo_Control_Characters_Or_Unbounded_Input()
    {
        var control = Assert.Throws<LinguaValidationException>(() => LinguaProjectCodes.EnsureValid("bad\ncode\u001b[31m"));
        Assert.DoesNotContain('\n', control.Message);
        Assert.DoesNotContain('\u001b', control.Message);

        var huge = Assert.Throws<LinguaValidationException>(() => LinguaProjectCodes.EnsureValid(new string('x', 5000) + "/"));
        Assert.True(huge.Message.Length < 500, $"错误信息过长：{huge.Message.Length}");
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    [InlineData("zh_Hans_CN")]
    [InlineData("sr-Latn-RS")]
    public void IsSafeCultureName_Should_Accept_Culture_Shapes(string culture)
    {
        Assert.True(LinguaCultureFiles.IsSafeCultureName(culture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("zh CN")]
    [InlineData("zh-CN.json")]
    [InlineData("中文")]
    public void IsSafeCultureName_Should_Reject_Path_Like_Or_Unsafe_Names(string? culture)
    {
        Assert.False(LinguaCultureFiles.IsSafeCultureName(culture));
    }

    [Fact]
    public void IsSafeCultureName_Should_Enforce_The_Length_Limit()
    {
        Assert.True(LinguaCultureFiles.IsSafeCultureName(new string('a', LinguaCultureFiles.MaxCultureNameLength)));
        Assert.False(LinguaCultureFiles.IsSafeCultureName(new string('a', LinguaCultureFiles.MaxCultureNameLength + 1)));
    }
}
