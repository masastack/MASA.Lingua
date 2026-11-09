namespace MASA.Lingua.Server.Services;

/// <summary>提示的语气：成功用绿色竖条，失败用红色竖条。</summary>
public enum ToastKind
{
    Success,
    Error
}

public sealed record ToastMessage(Guid Id, string Text, ToastKind Kind);

/// <summary>
/// 右下角浮动提示（每个 Blazor 电路一份，随电路销毁）。
/// 成功 3 秒、失败 6 秒后自动消失；失败可手动关闭。
/// 定时移除在后台线程执行，UI 订阅方负责切回渲染上下文（见 ToastHost）。
/// </summary>
public sealed class ToastService : IDisposable
{
    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ErrorLifetime = TimeSpan.FromSeconds(6);

    /// <summary>同时最多显示的条数：连续点几次翻译时挤掉最旧的一条，避免右下角堆成一片。</summary>
    private const int MaxVisible = 3;

    private readonly List<ToastMessage> _items = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _timers = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>列表变化通知；可能在后台线程触发。</summary>
    public event Action? Changed;

    public IReadOnlyList<ToastMessage> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    public void Success(string text) => Show(text, ToastKind.Success);

    public void Error(string text) => Show(text, ToastKind.Error);

    public void Dismiss(Guid id)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_timers.Remove(id, out cts))
            {
                return;
            }

            _items.RemoveAll(m => m.Id == id);
        }

        cts.Cancel();
        cts.Dispose();
        Changed?.Invoke();
    }

    private void Show(string text, ToastKind kind)
    {
        var message = new ToastMessage(Guid.NewGuid(), text, kind);
        var cts = new CancellationTokenSource();
        CancellationTokenSource? evicted = null;
        lock (_gate)
        {
            if (_disposed)
            {
                cts.Dispose();
                return;
            }

            if (_items.Count >= MaxVisible)
            {
                var oldest = _items[0];
                _items.RemoveAt(0);
                _timers.Remove(oldest.Id, out evicted);
            }

            _items.Add(message);
            _timers[message.Id] = cts;
        }

        if (evicted is not null)
        {
            evicted.Cancel();
            evicted.Dispose();
        }

        Changed?.Invoke();
        _ = RemoveAfterAsync(message.Id, kind == ToastKind.Error ? ErrorLifetime : SuccessLifetime, cts.Token);
    }

    private async Task RemoveAfterAsync(Guid id, TimeSpan lifetime, CancellationToken token)
    {
        try
        {
            await Task.Delay(lifetime, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Dismiss(id);
    }

    public void Dispose()
    {
        CancellationTokenSource[] timers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timers = [.. _timers.Values];
            _timers.Clear();
            _items.Clear();
        }

        foreach (var timer in timers)
        {
            timer.Cancel();
            timer.Dispose();
        }
    }
}
