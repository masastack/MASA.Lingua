using System.Text.Json;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Serialization;
using MASA.Lingua.Server.Services;
using MASA.Lingua.Storage.EfCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MASA.Lingua.Server.Endpoints;

public static class LinguaDistributionEndpoints
{
    /// <summary>SSE 心跳间隔：代理 / 负载均衡会掐掉长时间没有数据的连接，客户端也靠心跳发现断线。</summary>
    public static readonly TimeSpan SseHeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 导入请求体的大小上限：UI 文件选择限 5MB（MaxImportFileBytes），HTTP 端点留一倍余量。
    /// 没有上限时一个超大 POST 就能把整份字节读进内存再解析成词条树 —— 内存尖峰由外部请求直接触发。
    /// </summary>
    public const long MaxImportBodyBytes = 10 * 1024 * 1024;

    /// <summary>
    /// 带上限地读请求体：超过 <paramref name="cap"/> 立即停读返回 false（调用方回 413），
    /// 不再像 <c>CopyToAsync</c> 那样先把请求体整个吞进内存才发现太大。
    /// </summary>
    private static async Task<bool> TryCopyBodyWithCapAsync(HttpRequest request, MemoryStream target, long cap, CancellationToken ct)
    {
        // ContentLength 已知且超限时一个字节都不读
        if (request.ContentLength is { } length && length > cap)
        {
            return false;
        }

        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > cap)
            {
                return false;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return true;
    }

    public static IEndpointRouteBuilder MapLinguaDistributionEndpoints(this IEndpointRouteBuilder app)
    {
        // 1. 核心固定名静态文件端点：GET /i18n/{project}/{culture}.json
        // 支持浏览器 Disk Cache (max-age + stale-while-revalidate) 与 ETag / 304 Not Modified 零流量协商
        app.MapGet("/i18n/{project}/{culture}.json", ServeBundleAsync);

        // 2. 兼容 API 路径：GET /api/v1/bundles/{project}/{culture}
        app.MapGet("/api/v1/bundles/{project}/{culture}", ServeBundleAsync);

        // 2b. 项目发布清单：GET /api/v1/projects/{project}/manifest
        // 一次请求拿到线上版本指纹与各语种的地址 / ETag / 完成度；ETag 由版本指纹决定，一次 304 即知所有语种都没变。
        app.MapGet("/api/v1/projects/{project}/manifest", async (
            string project,
            HttpContext httpContext,
            StaticBundlePublisher publisher,
            AdoptionTracker adoption,
            CancellationToken ct) =>
        {
            var manifest = await publisher.GetManifestAsync(project, ct);
            if (manifest is null)
            {
                return Results.NotFound(new { error = $"Project '{project}' not found or not published yet." });
            }

            adoption.Record(project, httpContext.Request);

            // 必须每次向服务端确认（no-cache），确认的代价只是一次 304
            httpContext.Response.Headers.ETag = manifest.ETag;
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Append("Vary", "Origin");

            if (IfNoneMatchContains(httpContext.Request, manifest.ETag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Json(manifest);
        });

        // 2c. 生效术语（只读）：GET /api/v1/projects/{project}/glossary
        // 全局术语与项目术语合并后的结果（同名时项目覆盖全局），供 lingua-sync check 在本地 / CI 做术语检查。
        // 与 manifest 一样只读公开。
        app.MapGet("/api/v1/projects/{project}/glossary", async (
            string project,
            HttpContext httpContext,
            IGlossaryCatalog glossary,
            CancellationToken ct) =>
        {
            var terms = await glossary.GetEffectiveGlossaryByProjectCodeAsync(project, ct);
            if (terms is null)
            {
                return Results.NotFound(new { error = $"Project '{project}' not found." });
            }

            var payload = new
            {
                project,
                terms = terms.Select(t => new
                {
                    sourceTerm = t.SourceTerm,
                    description = t.Description,
                    scope = t.ProjectId is null ? "global" : "project",
                    doNotTranslate = t.DoNotTranslate,
                    caseSensitive = t.CaseSensitive,
                    targets = t.TargetTranslations,
                    acceptedVariants = t.AcceptedVariants,
                    forbidden = t.ForbiddenTranslations
                }).ToList()
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LinguaJson.Protocol);
            var etag = $"\"lingua-g-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16].ToLowerInvariant()}\"";
            httpContext.Response.Headers.ETag = etag;
            httpContext.Response.Headers.CacheControl = "no-cache";
            if (IfNoneMatchContains(httpContext.Request, etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Bytes(bytes, "application/json; charset=utf-8");
        });

        // 3. 缺失词条自动上报端点：POST /api/v1/telemetry/missing-keys
        // 唯一带鉴权的写端点：要求项目写密钥（X-Lingua-Key 头）。密钥无法安全保管在 MAUI / Vue 等
        // 不可信客户端，客户端 SDK 已移除上报能力——本端点只服务服务端 SDK（见 docs/integration/README.md 访问策略）。
        app.MapPost("/api/v1/telemetry/missing-keys", async (
            MissingKeyBatchReport report,
            HttpContext httpContext,
            IProjectCatalog projects,
            IEntryCatalog entryCatalog,
            CancellationToken ct) =>
        {
            // 鉴权只需要写密钥：轻量投影，不加载整棵词条树（这个端点会被业务服务高频调用）
            var project = await projects.GetProjectWriteAccessAsync(report.ProjectCode, ct);
            if (project is null)
            {
                return Results.NotFound(new { error = $"Project '{report.ProjectCode}' not found." });
            }

            var providedKey = httpContext.Request.Headers[WriteKeys.HeaderName].FirstOrDefault();
            if (string.IsNullOrEmpty(providedKey))
            {
                return Results.Unauthorized();
            }

            // SHA-256 后常量时间比较，避免逐字节比较的时序侧信道泄露密钥信息
            if (!WriteKeys.Matches(project.WriteKey, providedKey))
            {
                // Results.Forbid() 会走认证中间件语义（可能变成 challenge），这里要确定性的 403
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var added = await entryCatalog.RecordMissingKeysAsync(report, ct);
            return Results.Ok(new { project = report.ProjectCode, addedCount = added });
        });

        // 4. 项目骨架创建端点：POST /api/v1/projects/{project}
        // 与批量导入端点配套（import-json 对不存在的项目返回 404），让业务项目接入可以全程脚本化：
        // 创建 → 导入 → 发布 都走 HTTP。幂等：项目已存在时返回 200 且不改动任何配置。
        // 骨架默认值与实体一致：DefaultCulture zh-CN、SupportedCultures zh-CN/en-US/ja-JP、
        // SharedNamespaceCodes ["common"]；语种在导入时按需自动补齐（LinguaCultures.Resolve 校验 + 追加）。
        app.MapPost("/api/v1/projects/{project}", async (
            string project,
            IProjectCatalog projects,
            CancellationToken ct) =>
        {
            var code = project.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                return Results.BadRequest(new { error = "项目编码不能为空。" });
            }

            // 只判断存在性并回显默认语种：用摘要查询，不加载词条树
            var existing = await projects.GetProjectSummaryAsync(code, ct);
            if (existing is not null)
            {
                return Results.Ok(new { project = existing.Code, defaultCulture = existing.DefaultCulture, created = false });
            }

            var created = await projects.CreateProjectAsync(code, ct);
            // 新建时一并返回生成的写密钥（缺词上报鉴权），脚本化接入不需要再进管理后台查一次；
            // 已存在的项目不带密钥返回——要拿密钥去管理后台「接入密钥」面板。
            return Results.Ok(new { project = created.Code, defaultCulture = created.DefaultCulture, created = true, writeKey = created.WriteKey });
        });

        // 4b. 项目删除端点：DELETE /api/v1/projects/{project}
        // 与创建端点配套，供应用编码变更（如 PM 应用清单调整）后清理遗留项目：先删发布历史
        // （publish_snapshots 对 projects 是 ON DELETE RESTRICT），项目本体及其命名空间 / 词条 / 译文 / 术语
        // 由外键级联清除。项目不存在返回 404。磁盘上的发布产物（App_Data/i18n/{code}）不在此处清理。
        app.MapDelete("/api/v1/projects/{project}", async (
            string project,
            IProjectCatalog projects,
            CancellationToken ct) =>
        {
            var code = project.Trim();

            // 走仓储而不是直连数据库：仓储负责清发布历史 + 删项目的事务，并让读缓存随删除失效
            if (!await projects.DeleteProjectAsync(code, ct))
            {
                return Results.NotFound(new { error = $"Project '{code}' not found." });
            }

            return Results.Ok(new { project = code, deleted = true });
        });

        // 5. 存量项目 JSON 批量导入端点：POST /api/v1/projects/{project}/import-json?culture=zh-CN&defaultNs=&publish=false
        // 导入一律按「已审核」写入（存量文案默认可信）：译文会过占位符 / 长度 / HTML / 术语校验但只统计提示（FlaggedIssues），
        // 违规内容由发布门禁把关；默认不自动发布，publish=true 时导入后立即发布。
        // defaultNs 缺省或留空 = 项目根（顶层扁平 key 发布为 JSON 根级裸 key，客户端按裸 key 直查）。
        app.MapPost("/api/v1/projects/{project}/import-json", async (
            string project,
            string culture,
            string? defaultNs,
            bool? publish,
            HttpRequest request,
            IEntryCatalog entryCatalog,
            StaticBundlePublisher publisher,
            CancellationToken ct) =>
        {
            // 请求体上限：UI 文件选择限 5MB，这里留一倍余量；没有上限时一个超大 POST 就能把
            // 整份字节读进内存再解析成词条树，内存尖峰直接由外部请求触发（无需登录）。
            using var ms = new MemoryStream();
            if (!await TryCopyBodyWithCapAsync(request, ms, MaxImportBodyBytes, ct))
            {
                return Results.Json(
                    new { error = $"导入内容超过上限（{MaxImportBodyBytes / 1024 / 1024} MB），请拆分导入。" },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            JsonImportResult result;
            try
            {
                result = await entryCatalog.ImportJsonBundleAsync(
                    project,
                    culture,
                    ms.ToArray(),
                    string.IsNullOrWhiteSpace(defaultNs) ? "" : defaultNs,
                    ct);
            }
            catch (LinguaValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"不是合法的 JSON：{ex.Message}" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }

            var published = false;
            string? publishBlockedReason = null;
            if (publish == true)
            {
                try
                {
                    await publisher.PublishProjectAsync(project, $"导入 {result.Culture}.json 后发布", ct);
                    published = true;
                }
                catch (PublishBlockedException ex)
                {
                    // 导入已成功（草稿已写入），只是没有通过发布门禁
                    publishBlockedReason = ex.Message;
                }
            }

            return Results.Ok(new
            {
                result.ProjectCode,
                result.Culture,
                result.TotalParsed,
                result.AddedEntries,
                result.UpdatedTranslations,
                result.AffectedNamespaces,
                result.FlaggedIssues,
                result.StaleMarked,
                published,
                publishBlockedReason
            });
        });

        // 6. 全语种静态 JSON 压缩包导出端点：GET /api/v1/projects/{project}/export-zip
        app.MapGet("/api/v1/projects/{project}/export-zip", async (
            string project,
            string? format,
            HttpContext httpContext,
            StaticBundlePublisher publisher,
            CancellationToken ct) =>
        {
            bool isFlat = string.Equals(format, "flat", StringComparison.OrdinalIgnoreCase);
            try
            {
                var zipBytes = await publisher.ExportProjectZipAsync(project, isFlat, ct);
                return Results.File(zipBytes, "application/zip", $"{project}-i18n-json.zip");
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // 7. 发布事件实时推送 SSE 端点：GET /api/v1/stream?project=auth-web-staging
        // 每 15 秒发一次心跳注释行（": ping"），避免代理掐断空闲连接，也让客户端能及时发现断线并重连。
        app.MapGet("/api/v1/stream", async (
            string? project,
            HttpContext httpContext,
            LinguaSseBroadcaster broadcaster,
            IProjectCatalog projects,
            CancellationToken ct) =>
        {
            // 必须指定项目：避免一个连接收到所有项目的事件
            if (string.IsNullOrWhiteSpace(project))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { error = "缺少 project 参数。" }, ct);
                return;
            }

            // 订阅与补发必须用同一个归一化后的键：
            // 否则 "auth-web-staging " 能通过校验，却因为广播侧的 filter 匹配不上而永远收不到事件。
            var projectKey = project.Trim();

            // 项目必须存在：查不到就拒绝，避免无意义的进程内订阅长期占用
            if (await projects.GetProjectSummaryAsync(projectKey, ct) is null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                await httpContext.Response.WriteAsJsonAsync(new { error = $"项目不存在：{projectKey}" }, ct);
                return;
            }

            httpContext.Response.Headers.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";

            var (subId, reader) = broadcaster.Subscribe(projectKey);
            try
            {
                // 建议客户端 3 秒后重连；首个字节立即发出，让客户端确认连接成功
                await httpContext.Response.WriteAsync("retry: 3000\n: connected\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);

                // 断线补发：客户端（EventSource 会自动带 Last-Event-ID）上次收到的 id 落后于最近一次发布时，
                // 立刻补发那一条，避免「重连成功但错过了发布」只能等 60s 轮询兜底。
                if (httpContext.Request.Headers.TryGetValue("Last-Event-ID", out var lastIdHeader) &&
                    long.TryParse(lastIdHeader.ToString(), out var lastEventId) &&
                    broadcaster.TryGetLast(projectKey, out var missed) &&
                    missed.EventId > lastEventId)
                {
                    var missedJson = JsonSerializer.Serialize(missed, LinguaJson.Protocol);
                    await httpContext.Response.WriteAsync($"id: {missed.EventId}\ndata: {missedJson}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }

                while (!ct.IsCancellationRequested)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    heartbeat.CancelAfter(SseHeartbeatInterval);

                    try
                    {
                        if (!await reader.WaitToReadAsync(heartbeat.Token))
                        {
                            break;
                        }

                        while (reader.TryRead(out var evt))
                        {
                            var json = JsonSerializer.Serialize(evt, LinguaJson.Protocol);
                            await httpContext.Response.WriteAsync($"id: {evt.EventId}\ndata: {json}\n\n", ct);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await httpContext.Response.WriteAsync(": ping\n\n", ct);
                    }

                    await httpContext.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // 客户端正常断开
            }
            finally
            {
                broadcaster.Unsubscribe(subId);
            }
        });

        return app;
    }

    private static async Task<IResult> ServeBundleAsync(
        string project,
        string culture,
        string? format,
        HttpContext httpContext,
        StaticBundlePublisher publisher,
        AdoptionTracker adoption,
        CancellationToken ct)
    {
        var bundle = await publisher.GetOrCompileBundleAsync(project, culture, ct);
        if (bundle is null)
        {
            return Results.NotFound(new { error = $"Bundle '{project}/{culture}' not found or not published yet." });
        }

        adoption.Record(project, httpContext.Request);

        bool isFlat = string.Equals(format, "flat", StringComparison.OrdinalIgnoreCase);

        // 两种格式刻意返回同一个 ETag（由同时覆盖嵌套 / 扁平字节的内容指纹派生）：
        // SDK 用 manifest / SSE 事件里的 ETag 与下载响应的 ETag 对账，且 .NET 客户端按扁平格式下载、
        // 嵌套 / 扁平共一个 ETag 才能互相衔接（否则任一格式变更都会让另一格式的对账永远不命中）。
        // HTTP 缓存按完整 URL（含 format 查询参数）分条目，不会把两种格式混进同一个缓存条目。
        var etag = bundle.ETag;
        httpContext.Response.Headers.ETag = etag;
        // 一律 no-cache（每次用前必须用 ETag 重新校验）：1024 字节的 304 很便宜，
        // 换来「发布完成 = 客户端下次请求立即看到新文案」，不再有 max-age/SWR 造成的旧内容窗口。
        httpContext.Response.Headers.CacheControl = "public, no-cache";
        httpContext.Response.Headers.Append("Vary", "Origin");

        if (IfNoneMatchContains(httpContext.Request, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var payload = isFlat ? bundle.FlatJsonUtf8Bytes : bundle.NestedJsonUtf8Bytes;
        return ByteMemoryResult.Create(payload, "application/json; charset=utf-8");
    }

    private static bool IfNoneMatchContains(HttpRequest request, string etag)
    {
        var header = request.Headers.IfNoneMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        return header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(tag => tag == "*" || string.Equals(tag.TrimStart('W', '/'), etag, StringComparison.Ordinal));
    }
}
