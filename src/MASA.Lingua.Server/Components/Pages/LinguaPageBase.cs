using Microsoft.AspNetCore.Components;

namespace MASA.Lingua.Server.Components.Pages;

/// <summary>
/// 管理后台页面共用的基类：状态提示 + 数据加载守卫。
/// <para>
/// 三个页面以前各写一套（Home 的 <c>_statusText/_statusIsError/_statusMessage</c>、
/// Glossary 的 <c>_message/_messageIsError</c>、ImportExport 的 <c>_notice</c>），
/// 语义还不一致：有的成功/失败同色、有的失败会被普通提示悄悄清掉错误样式。
/// 这里统一成一套约定：
/// </para>
/// <list type="bullet">
/// <item>给 <see cref="_statusMessage"/> 赋值 = 普通提示（自动清掉错误样式）；</item>
/// <item>失败信息一律用 <see cref="ShowStatusError"/>，以错误样式显示；</item>
/// <item><see cref="ClearStatus"/> 用于页面切换等场景主动清空。</item>
/// </list>
/// <para>
/// 成员名沿用 Home 页原有约定（下划线字段），这样迁移时页面标记里已有的绑定不需要改一行。
/// </para>
/// <para>
/// 加载守卫同样三个页面各写了一套（加载代数 + <c>_disposed</c> + 加载骨架标记）：连续切换项目时，
/// 较早发起、较晚返回的加载会覆盖新项目的数据，必须靠代数丢弃。约定：
/// </para>
/// <list type="bullet">
/// <item>加载开始调 <see cref="NextLoadVersion"/> 拿代数（并在需要时自己置 <see cref="_isLoading"/>）；</item>
/// <item>查询返回、要把结果写进页面状态之前用 <see cref="IsCurrent"/> 确认自己仍是最新一次加载；</item>
/// <item>无论成败都在 finally 里调 <see cref="EndLoad"/>——只有最新一次加载能结束加载态，被取代的旧加载不能提前亮出半成品。</item>
/// </list>
/// </summary>
public abstract class LinguaPageBase : ComponentBase, IDisposable
{
    /// <summary>
    /// 加载代数：每次发起加载 +1。异步加载返回时用它判断自己是否已被更新的加载取代。
    /// 只在电路（同步上下文）里读写，不需要原子操作。
    /// </summary>
    protected int _loadVersion;

    /// <summary>组件已释放（电路断开）：后台线程算完的结果不再写回页面状态。</summary>
    protected bool _disposed;

    /// <summary>加载骨架标记：数据到位前不渲染旧项目的内容（首次进入也从加载态开始，避免闪出「没有数据」）。</summary>
    protected bool _isLoading = true;

    /// <summary>当前提示文本；null 表示不显示提示条。</summary>
    protected string? _statusText;

    /// <summary>提示条是否以错误样式显示。</summary>
    protected bool _statusIsError;

    /// <summary>开始一次加载：返回本次加载的代数。是否进入加载态由调用方决定（切项目要骨架，页面内保存后重载不要）。</summary>
    protected int NextLoadVersion() => ++_loadVersion;

    /// <summary>本次加载的结果仍然有效（没有被更新的加载取代，页面也还在）。</summary>
    protected bool IsCurrent(int version) => version == _loadVersion && !_disposed;

    /// <summary>结算一次加载：只有最新一次加载能结束加载态；被取代的旧加载不得提前亮出半成品。</summary>
    protected void EndLoad(int version)
    {
        if (version == _loadVersion)
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// 在一个有返回值的后台任务里安全地改页面状态：把 <paramref name="work"/> 调度回本组件的同步上下文执行并等它完成。
    /// <para>
    /// <see cref="ComponentBase.InvokeAsync(Action)"/> / <see cref="ComponentBase.InvokeAsync(Func{Task})"/> 都不带返回值，
    /// 而后台线程常常需要「改完状态再把结果带回去」（例如 AI 写回后要报告哪些词条在内存里找不到）。
    /// </para>
    /// </summary>
    protected Task<T> DispatchAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = InvokeAsync(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }

            return Task.CompletedTask;
        });

        return completion.Task;
    }

    /// <summary>普通提示（会清掉错误样式）。</summary>
    protected string? _statusMessage
    {
        get => _statusText;
        set
        {
            _statusText = value;
            _statusIsError = false;
        }
    }

    protected void ShowStatusError(string message)
    {
        _statusText = message;
        _statusIsError = true;
    }

    protected void ClearStatus()
    {
        _statusText = null;
        _statusIsError = false;
    }

    /// <summary>
    /// 释放：标记已释放（后台计算完成后不再写回页面状态）并给子类留一个解绑事件的钩子。
    /// 页面原本各自 <c>@implements IDisposable</c>，现在统一在基类实现。
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        OnDisposing();
        GC.SuppressFinalize(this);
    }

    /// <summary>子类在这里解绑事件订阅（例如顶栏的项目切换通知）。</summary>
    protected virtual void OnDisposing()
    {
    }
}
