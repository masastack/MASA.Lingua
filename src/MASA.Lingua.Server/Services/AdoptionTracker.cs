using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace MASA.Lingua.Server.Services;

/// <summary>某个版本当前有多少个业务端实例在使用。</summary>
public sealed record AdoptionVersionRow(string Version, int Instances, IReadOnlyList<string> Kinds);

public sealed record AdoptionSummary(int TotalInstances, IReadOnlyList<AdoptionVersionRow> Versions)
{
    public static AdoptionSummary Empty { get; } = new(0, []);
}

/// <summary>
/// 采用情况：SDK 拉取 manifest / bundle 时带上
/// <c>X-Lingua-Client: {kind}/{实例标识}</c>（如 aspnetcore/3f2a…、vue/ab12…）与 <c>X-Lingua-Version: {当前持有的版本}</c>，
/// 这里据此统计「各版本现在有多少实例在用」，发布后能看到业务端有没有真的更新、有没有人卡在旧版本。
/// 只在内存里保留最近 24 小时活跃的实例，每个项目最多 2000 个，超出时淘汰最久没出现的。
/// </summary>
public sealed class AdoptionTracker
{
    public const string ClientHeader = "X-Lingua-Client";
    public const string VersionHeader = "X-Lingua-Version";
    public const string NoVersion = "(未知)";

    public static readonly TimeSpan ActiveWindow = TimeSpan.FromHours(24);
    public const int MaxInstancesPerProject = 2000;

    private sealed record Instance(string Kind, string Version, DateTimeOffset LastSeen);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Instance>> _projects = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public AdoptionTracker() : this(TimeProvider.System)
    {
    }

    public AdoptionTracker(TimeProvider time) => _time = time;

    /// <summary>从请求头记录一次采用情况；没有 X-Lingua-Client 头的请求（浏览器直接访问等）不计入。</summary>
    public void Record(string projectCode, HttpRequest request)
    {
        var client = request.Headers[ClientHeader].ToString();
        if (string.IsNullOrWhiteSpace(client))
        {
            return;
        }

        Record(projectCode, client, request.Headers[VersionHeader].ToString());
    }

    public void Record(string projectCode, string client, string? version)
    {
        client = Truncate(client.Trim(), 96);
        var slash = client.IndexOf('/');
        var kind = slash > 0 ? client[..slash] : client;
        var instanceId = slash > 0 ? client : client + "/-";
        var v = string.IsNullOrWhiteSpace(version) ? NoVersion : Truncate(version.Trim(), 64);

        var instances = _projects.GetOrAdd(projectCode, _ => new ConcurrentDictionary<string, Instance>(StringComparer.Ordinal));
        var now = _time.GetUtcNow();
        instances[instanceId] = new Instance(Truncate(kind, 32), v, now);

        if (instances.Count > MaxInstancesPerProject)
        {
            Prune(instances, now);
        }
    }

    public AdoptionSummary GetSummary(string projectCode)
    {
        if (!_projects.TryGetValue(projectCode, out var instances))
        {
            return AdoptionSummary.Empty;
        }

        var cutoff = _time.GetUtcNow() - ActiveWindow;
        var active = instances.Values.Where(i => i.LastSeen >= cutoff).ToList();
        var rows = active
            .GroupBy(i => i.Version)
            .Select(g => new AdoptionVersionRow(
                g.Key,
                g.Count(),
                g.Select(i => i.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()))
            .OrderByDescending(r => r.Instances)
            .ThenBy(r => r.Version, StringComparer.Ordinal)
            .ToList();

        return new AdoptionSummary(active.Count, rows);
    }

    private void Prune(ConcurrentDictionary<string, Instance> instances, DateTimeOffset now)
    {
        var cutoff = now - ActiveWindow;
        foreach (var (id, inst) in instances)
        {
            if (inst.LastSeen < cutoff)
            {
                instances.TryRemove(id, out _);
            }
        }

        var excess = instances.Count - MaxInstancesPerProject;
        if (excess <= 0)
        {
            return;
        }

        foreach (var (id, _) in instances.OrderBy(kv => kv.Value.LastSeen).Take(excess).ToList())
        {
            instances.TryRemove(id, out _);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
