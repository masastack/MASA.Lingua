using MASA.Lingua.Core.Formatting;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 占位符词法必须与校验器一致（都认 {0} 与 {name}）。以前格式化器只认字母开头，
/// 导致「校验通过（含 {0}）但运行时插值不生效」。
/// </summary>
public sealed class PlaceholderTests
{
    [Fact]
    public void Positional_Placeholder_Should_Be_Interpolated()
    {
        Assert.Equal("已选 5 件商品", LinguaInterpolation.Format("已选 {0} 件商品", 5));
    }

    [Fact]
    public void Out_Of_Order_Positional_Placeholders_Should_Map_By_Index_Not_By_Appearance()
    {
        // 按出现顺序取值会串位（b - a），必须按数字索引
        Assert.Equal("b - a", LinguaInterpolation.Format("{1} - {0}", "a", "b"));
    }

    [Fact]
    public void Named_Placeholder_Should_Still_Work_With_Object()
    {
        Assert.Equal("欢迎回来，Masa！", LinguaInterpolation.Format("欢迎回来，{userName}！", new { userName = "Masa" }));
    }

    [Fact]
    public void Missing_Placeholder_Argument_Should_Keep_Token()
    {
        Assert.Equal("已选 {0} 件", LinguaInterpolation.Format("已选 {0} 件"));
    }
}
