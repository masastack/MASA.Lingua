using System.Reflection;
using MASA.Lingua.Server.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 管理后台授权默认值：Pages 目录下的每个可路由组件都必须带 [Authorize]
/// （由 <c>Components/Pages/_Imports.razor</c> 统一注入）。
/// 这条测试防的是「新增页面忘了标 [Authorize] → 页面匿名可访问」这类退化。
/// </summary>
public sealed class PageAuthorizationTests
{
    [Fact]
    public void Every_Routable_Component_Should_Require_Authorization()
    {
        var pages = typeof(App).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && t.GetCustomAttributes<RouteAttribute>(inherit: true).Any())
            .ToList();

        // 一个页面都没扫到说明反射前提变了（例如组件被挪到别的程序集），测试本身失效必须暴露出来
        Assert.NotEmpty(pages);

        var anonymous = pages
            .Where(t => !t.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any())
            .Select(t => t.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            anonymous.Count == 0,
            "以下页面没有 [Authorize]（应由 Components/Pages/_Imports.razor 统一提供）："
            + string.Join(", ", anonymous));
    }
}
