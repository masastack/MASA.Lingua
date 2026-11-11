using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.JSInterop;
using MASA.Lingua.Server.Services;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// CrashReporter 良性噪音判定：只允许「已核实无功能影响」的两类模式被降级为单行 NOISE
/// （Masa.Blazor OutsideClickJSModule 的 null→bool、电路断开后的 JSDisconnectedException），
/// 其余 —— 含同为 Masa.Blazor 库噪音但影响未知的 InputJSModule / NavigationDrawer 模式 ——
/// 必须保持完整记录，宁可多记不可漏记。
/// </summary>
public sealed class CrashReporterTests
{
    /// <summary>
    /// 测试专用替身：CrashReporter 按异常栈文本匹配 Masa.Blazor 库内调用，
    /// 这里用同名方法真实抛出，以复刻库异常的栈帧现场（命名必须保持成对出现）。
    /// </summary>
    private static class OutsideClickJSModule
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void UpdateDependentElementsAsync()
        {
            throw new JSException(
                "An exception occurred executing JS interop: The JSON value could not be converted to System.Boolean.",
                new JsonException("The JSON value could not be converted to System.Boolean."));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SomeOtherInteropSite()
    {
        throw new JSException(
            "An exception occurred executing JS interop: The JSON value could not be converted to System.Boolean.",
            new JsonException("The JSON value could not be converted to System.Boolean."));
    }

    private static T Captured<T>(Action thrower) where T : Exception
    {
        try
        {
            thrower();
        }
        catch (T ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"未抛出预期的 {typeof(T).Name}");
    }

    [Fact]
    public void OutsideClick_null_to_bool_should_be_demoted()
    {
        var unobserved = new AggregateException(Captured<JSException>(OutsideClickJSModule.UpdateDependentElementsAsync));

        var demoted = CrashReporter.TryDescribeKnownBenignNoise(unobserved, out var noise);

        Assert.True(demoted);
        Assert.Contains("OutsideClickJSModule", noise);
        Assert.Contains("no functional impact", noise);
    }

    [Fact]
    public void Null_to_bool_from_other_interop_site_should_stay_full_fidelity()
    {
        // 同样的 null→bool 形态，但不是库内那个调用点（比如应用自己的 interop 声明错返回类型）—— 影响未知，必须完整记录
        var unobserved = new AggregateException(Captured<JSException>(SomeOtherInteropSite));

        Assert.False(CrashReporter.TryDescribeKnownBenignNoise(unobserved, out _));
    }

    [Fact]
    public void JsWithoutJsonInner_should_stay_full_fidelity()
    {
        var unobserved = new AggregateException(
            Captured<JSException>(() => throw new JSException("JS error without Json inner")));

        Assert.False(CrashReporter.TryDescribeKnownBenignNoise(unobserved, out _));
    }

    [Fact]
    public void Circuit_disconnected_should_be_demoted()
    {
        var nested = new AggregateException(new JSDisconnectedException("circuit disconnected"));
        var doubleNested = new AggregateException(nested);

        Assert.True(CrashReporter.TryDescribeKnownBenignNoise(nested, out var noise));
        Assert.Contains("circuit", noise);
        Assert.True(CrashReporter.TryDescribeKnownBenignNoise(doubleNested, out _));

        // 直接裸异常（非聚合）也要能识别
        Assert.True(CrashReporter.TryDescribeKnownBenignNoise(
            new JSDisconnectedException("circuit disconnected"), out _));
    }

    [Fact]
    public void Input_argument_null_should_stay_full_fidelity()
    {
        // crash.log 里 224 条的 InputJSModule.SetValue 模式：输入值可能没同步成功，影响未知
        var unobserved = new AggregateException(new ArgumentNullException("_instance"));

        Assert.False(CrashReporter.TryDescribeKnownBenignNoise(unobserved, out _));
    }

    [Fact]
    public void Disposal_null_reference_should_stay_full_fidelity()
    {
        // crash.log 里 136 条的 NavigationDrawer dispose NRE 模式：影响未知
        var unobserved = new AggregateException(new NullReferenceException());

        Assert.False(CrashReporter.TryDescribeKnownBenignNoise(unobserved, out _));
    }

    [Fact]
    public void Mixed_aggregate_should_stay_full_fidelity()
    {
        // 聚合里混入任何一个未知内层，整体按未知处理
        var mixed = new AggregateException(
            new JSDisconnectedException("circuit disconnected"),
            new InvalidOperationException("unknown"));

        Assert.False(CrashReporter.TryDescribeKnownBenignNoise(mixed, out _));
    }
}
