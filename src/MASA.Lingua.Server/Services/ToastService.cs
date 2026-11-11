using Masa.Blazor;
using Masa.Blazor.Presets;
using Microsoft.JSInterop;

namespace MASA.Lingua.Server.Services;

/// <summary>
/// 右下角浮动提示（每个 Blazor 电路一份，随电路销毁）。
/// 迁移 Masa.Blazor 后：转发到 <see cref="IPopupService"/> 的全局 Snackbar 队列，
/// 由 MApp 根槽位自动渲染的 EnqueuedSnackbars 渲染堆叠（见 MainLayout 里的说明）。
/// 成功 3 秒自动消失；失败 6 秒且带手动关闭按钮（对应原 ToastService 的两条生命期）。
/// 测试宿主没有注册 IPopupService：构造参数走默认值，提示静默丢弃（页面测试只关心页面状态）。
/// </summary>
public sealed class ToastService(IPopupService? popup = null)
{
    private const int SuccessLifetimeMs = 3000;
    private const int ErrorLifetimeMs = 6000;

    public void Success(string text) => Show(text, AlertTypes.Success, SuccessLifetimeMs, closeable: false);

    public void Error(string text) => Show(text, AlertTypes.Error, ErrorLifetimeMs, closeable: true);

    private void Show(string text, AlertTypes type, int timeoutMs, bool closeable)
    {
        if (popup is null)
        {
            return; // 未注册全局弹窗服务（单元测试宿主）：没有可用的渲染宿主
        }

        // fire-and-forget 必须自己接住异常：用户关标签页 / 刷新时电路销毁，Snackbar 的
        // 渲染-定位链会抛 JSDisconnectedException，没人观察就会在 GC finalizer 里重抛、刷 LINGUA-WARNING 日志。
        _ = ShowAsync(popup, new SnackbarOptions
        {
            Content = text,
            Type = type,
            Timeout = timeoutMs,
            Closeable = closeable,
        });
    }

    private static async Task ShowAsync(IPopupService popup, SnackbarOptions options)
    {
        try
        {
            await popup.EnqueueSnackbarAsync(options);
        }
        catch (JSDisconnectedException)
        {
            // 电路已断开：提示无处可显示，丢弃即可
        }
        catch (ObjectDisposedException)
        {
            // 电路正在销毁：同上
        }
    }
}
