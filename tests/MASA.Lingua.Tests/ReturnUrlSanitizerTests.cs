using MASA.Lingua.Server.Startup;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>登录 returnUrl 净化：只放行站内相对路径，任何开放重定向变体一律回落 "/"。</summary>
public sealed class ReturnUrlSanitizerTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/glossary", "/glossary")]
    [InlineData("/import-export?project=shop-vue", "/import-export?project=shop-vue")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("//evil.com", "/")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("%2F%2Fevil.com", "/")]
    [InlineData("http://evil.com", "/")]
    [InlineData("https://evil.com/x", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/a%00b", "/")]
    [InlineData("glossary", "/")]
    public void Sanitize_Should_Only_Allow_Local_Paths(string? input, string expected)
    {
        Assert.Equal(expected, ReturnUrlSanitizer.Sanitize(input));
    }
}
