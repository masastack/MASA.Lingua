namespace MASA.Lingua.Server.Services;

/// <summary>
/// 管理后台当前选中的项目范围：由顶栏的项目切换器设置，词条矩阵 / 术语库 / 导入与发布三个页面共用。
/// 注册为 Scoped：Blazor Server 下每个浏览器会话（电路）各持有一份，互不影响。
/// </summary>
public sealed class WorkspaceScope
{
    /// <summary>「全局共享命名空间」伪项目的编码（仅词条矩阵可编辑）。</summary>
    public const string Shared = "__shared__";

    /// <summary>默认项目：内置示例项目之一（真实业务词条），避免首次进入落在已不存在的编码上。</summary>
    public string ProjectCode { get; private set; } = "auth-web-staging";

    public bool IsShared => ProjectCode == Shared;

    /// <summary>选中的项目发生变化（包括因原项目不存在而被自动纠正）。</summary>
    public event Action? Changed;

    /// <summary>选中变化返回 true（含被自动纠正）；传入空串或与当前相同则不变化返回 false。</summary>
    public bool Select(string projectCode)
    {
        if (string.IsNullOrWhiteSpace(projectCode) || projectCode == ProjectCode)
        {
            return false;
        }

        ProjectCode = projectCode;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 以 PM 应用清单为合法集合纠正当前选择：PM 是项目来源（Lingua 本地库只存多语言配置，
    /// 不决定项目清单），本地是否已有该应用的配置不影响合法性——未初始化的应用照样可选，
    /// 由界面提示用户显式创建配置。
    /// </summary>
    public void EnsureValid(IReadOnlyCollection<string> pmAppIdentities)
    {
        if (IsShared || pmAppIdentities.Contains(ProjectCode))
        {
            return;
        }

        ProjectCode = pmAppIdentities.FirstOrDefault() ?? Shared;
        Changed?.Invoke();
    }

    /// <summary>
    /// 主动触发 Changed（如「创建多语言配置」按钮成功后），让监听页面重载当前项目数据。
    /// </summary>
    public void NotifyChanged() => Changed?.Invoke();
}
