# 部署

> [English](deployment.md) · [返回文档目录](README.md)

Lingua 服务端的生产部署清单。客户端 SDK 的部署见[接入指南](integration/README.md)。

## 要求

| 项 | 要求 | 说明 |
|---|---|---|
| 运行时 | .NET 10 | 服务端依赖闭包（`Server` / `Storage.*` / `AI`）单目标 net10 |
| 数据库 | PostgreSQL（唯一支持） | 连接串来自 `Lingua:Database:ConnectionString`（或 `ConnectionStrings:LinguaDb`） |
| 副本数 | **单实例** | 发布缓存、manifest 统计与 SSE 广播都是进程内状态；多副本会静默出错（见[架构](architecture.zh-CN.md#单实例约束)） |
| 发布产物目录 | `Lingua:BundleDirectory`（默认 `{ContentRoot}/App_Data/i18n`） | 只用于重启恢复，不对公网暴露（`/i18n/*` 永远走分发端点） |
| 管理后台认证 | SSO 强制登录 | `Auth:ClientId` + DCC（`DCC` 段，MasaStackConfig 自动解析 SSO 域名），任一缺失即启动失败；`Auth:Authority` 仅限测试/离线调试覆盖 |
| HTTPS | 管理后台必须 | 在 ingress 终止 TLS + `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` |

## 容器镜像

[Dockerfile](../src/MASA.Lingua.Server/Dockerfile) 面向生产：

- 非 root 运行，钉住 **UID 1000**（`appuser`），对齐运维挂卷属主惯例。
- `/data` 下内置可写目录（权限 `1777`，任意运行 UID 可写）：

| 路径 | 用途 | 持久化 |
|---|---|---|
| `/data/i18n` | 已发布产物（env `Lingua__BundleDirectory`） | 可丢弃——容器重建后从数据库快照补回 |
| `/data/dataprotection` | DataProtection 密钥环（认证 Cookie、OIDC 状态） | **必须挂卷**——不挂则容器重建后全员登出 |
| `/data/logs` | 文件日志 + 崩溃记录（`crash.log`） | 建议挂卷，重启后可回溯 |
| `/data/dumps` | 未处理异常的 minidump（`DOTNET_DbgMiniDump*`） | 建议挂卷 |

- 支持 `readOnlyRootFilesystem: true`：把上述路径挂成卷/`emptyDir`（K8s 用 `fsGroup` 处理卷属主）。
- 端口：**8080**（HTTP）与 8081（HTTPS），来自 `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_HTTPS_PORTS`。

## HTTPS 与转发头

管理后台 OIDC 的 correlation/nonce Cookie 带 `Secure` 标记——纯 HTTP 下浏览器拒收，**登录无法完成**。设置：

```yaml
env:
  - name: ASPNETCORE_FORWARDEDHEADERS_ENABLED
    value: "true"   # 让应用采纳 ingress 的 X-Forwarded-Proto
```

80→443 跳转由边缘（ingress-nginx 默认 `ssl-redirect: true`）完成。容器内发现不了 https 端口而需要兜底时，设 `ASPNETCORE_HTTPS_PORT=443`。

## 内存与韧性

镜像内置针对导入/发布内存尖峰调优的 GC 配置：

```dockerfile
ENV DOTNET_GCHeapHardLimitPercent=50   # 堆硬上限钉在 cgroup limit 的一半——宁可 GC 多跑，不做无痕死亡
ENV DOTNET_GCDynamicAdaptationMode=0   # DAC 启发式在尖峰式分配下反应滞后
```

未处理异常会在 `/data/dumps` 产 minidump、在 `/data/logs` 产 `crash.log`；文件日志按天滚动（保留 14 份、单份 50MB 上限），事故回放不随 Pod 重启丢失。

## 访问模型

- 机器**读**端点（`/i18n/*`、manifest、术语库、SSE）匿名且 CORS 全开（`Access-Control-Allow-Origin: *`，暴露 `ETag`）。请部署在受控网段；发布产物公开是设计决定。
- **写**端点独立控制：项目创建/删除与导入靠网段管控（端口不要对公网开放），缺词上报要求项目写密钥、是唯一带鉴权的机器端点。
- 管理后台（`/`、`/glossary`、`/import-export`）强制 SSO 登录——与机器端点是两套体系。

## CI/CD 拓扑

- **GitHub** 是源仓库：PR CI（`pr_run_test_ci.yml`）、GitHub Release 触发 NuGet 发布（`package_push_nuget.org.yml`）。
- **GitLab** 接收镜像（`mirror-gitlab.yml`）并执行部署流水线（`.gitlab-ci.yml`）：镜像构建 → `push-sec` → `push-prd`，走 MASA Stack 的 ArgoCD 模板。
