# 安全策略

[English](SECURITY.md)

## 支持的版本

| 版本 | 是否支持 |
|---|---|
| 1.3.x | 是 |
| < 1.3 | 否 |

## 报告漏洞

请**不要**通过公开的 GitHub Issue 报告安全漏洞。

请改用 [GitHub Security Advisories](https://github.com/masastack/MASA.Lingua/security/advisories/new) 私密报告（「Report a vulnerability」）。请尽量包含：

- 问题类型（如注入、鉴权绕过、信息泄露）
- 影响的组件（服务端分发端点、管理后台、SDK、CLI）
- 逐步复现步骤或 PoC
- 影响评估

我们的目标是 5 个工作日内确认收到报告，并向你同步修复进展。

## 安全模型说明

部署规划时请知悉平台文档化的访问模型（详见 [docs/deployment.zh-CN.md](docs/deployment.zh-CN.md)）：

- 机器**读**端点（发布产物、manifest、术语库）按设计匿名且 CORS 全开——本服务面向受控网段部署，不适合把管理面直接暴露公网。
- **写**端点（项目创建/删除、导入）独立控制；缺词上报端点要求项目写密钥。
- 管理后台强制 SSO（OIDC）登录，且必须以 HTTPS 提供服务。

以上是文档化的设计决策，不属于漏洞；对它们的意见请先走常规 Issue。
