# lingua-sync 命令行

随 `@lingua/vue` 一起提供（`packages/lingua-vue/bin/lingua-sync.mjs`），Node ≥ 18，**不依赖 Vue**
（构建期拉兜底包与前端框架无关）。三个命令：

| 命令 | 作用 |
|---|---|
| `pull` | 把线上**已发布**版本拉到本地目录，写 `lingua.lock.json`（构建期兜底包） |
| `push` | 把存量的 `{culture}.json` 导入平台（**只写草稿**，除非 `--publish`） |
| `check` | 完成度 / 占位符一致性 / 源码 key 扫描 / 术语遵循检查（CI 用，不需要服务端也能跑一半） |

## 运行

安装了 `@lingua/vue` 之后直接跑（CLI 随包发布，`bin` 指向 `lingua-sync.mjs`）：

```bash
npx lingua-sync pull
npx lingua-sync check --remote
```

也可以装成开发依赖并写进 npm scripts：

```jsonc
// package.json
"devDependencies": { "@lingua/vue": "^1.1.0" },
"scripts": {
  "i18n:pull": "lingua-sync pull",
  "i18n:check": "lingua-sync check"
}
```

在 Lingua 仓库内开发时可直连源码跑：`node packages/lingua-vue/bin/lingua-sync.mjs <命令>`。

不带子命令、只给 `--endpoint` / `--project` 时按旧用法当作 `pull`。

## 配置

优先级：**命令行参数 > 环境变量 > `lingua.config.json` > 默认值**。

> 一个例外：`cultures` 的解析顺序是 `--cultures` > 配置文件 > `LINGUA_CULTURES`
> （`lib/cli.mjs` 的 `resolveConfig`），环境变量优先级低于配置文件，与其余键相反。

```jsonc
// lingua.config.json（放项目根目录）
{
  "endpoint": "https://lingua.example.com",
  "project": "shop-vue",
  "cultures": ["zh-CN", "en-US", "ja-JP"],   // 可选：只处理这些语种
  "out": "src/locales",                       // 可选：本地语言包目录
  "src": "src",                               // 可选：check 扫描的源码目录
  "defaultCulture": "zh-CN",                  // 可选：check 的源语种，缺省取 manifest / lingua.lock.json
  "dynamicKeyPrefixes": ["order.status."]     // 可选：动态拼接 key 的前缀白名单
}
```

| 参数 | 环境变量 | 说明 |
|---|---|---|
| `--endpoint` | `LINGUA_ENDPOINT` | 服务地址（不配默认 `http://localhost:5000`） |
| `--project` | `LINGUA_PROJECT` | 项目编码 |
| `--cultures a,b` | `LINGUA_CULTURES` | 只处理这些语种（逗号分隔，也可在配置文件里写成数组） |
| `--default-culture` | — | `check` 的源语种；缺省按 manifest（`--remote`）或 `lingua.lock.json` 的 `defaultCulture` 推断 |
| `--out <dir>` | — | 本地语言包目录，默认 `src/locales` |
| `--src <dir>` | — | `check` 扫描的源码目录，默认 `src` |
| `--config <file>` | — | 配置文件，默认 `./lingua.config.json` |

退出码：`0` 成功；`1` 失败（请求出错，或 `check` 未通过）。

## pull：拉取线上已发布版本

```bash
lingua-sync pull
# [lingua-sync] 已写入 src\locales\zh-CN.json（10 条）
# [lingua-sync] 已写入 src\locales\en-US.json（10 条）
# [lingua-sync] 已写入 src\locales\ja-JP.json（10 条）
# [lingua-sync] 完成：版本 33d9032d2836860d，lock 已写入 src\locales\lingua.lock.json
```

- 拉取平台**已发布**的全部语种（`--cultures` 可限定），写到 `--out` 目录，同时写 `lingua.lock.json`
  记录版本指纹、manifest ETag 与各语种 ETag。
- 第二次 `pull` 会带 `If-None-Match`：manifest 304 且 local 文件齐全时直接输出「已是最新版本」并返回，
  不再逐语种请求。
- 用途：作为 Vue 的 `staticBundles`（见 [vue.md](vue.md)）、Blazor Wasm 的 `wwwroot/i18n/` 兜底、或 .NET 项目的 `i18n/` 离线初始包。
- 构建流水线里先 `pull` 再打包，这样产物里的文案和发布版本对应，且平台临时不可用时应用仍能显示。
- 需要「与线上完全一致的快照」时用 `pull`，**不要**用 `GET …/export-zip`——后者导出的是数据库当前编辑状态（含未发布草稿）。

## push：导入存量语言包

```bash
lingua-sync push --dir src/locales      # 只写草稿
lingua-sync push --dir src/locales --default-ns ''   # 顶层扁平 key 归入项目根（发布成 JSON 根级裸 key）
lingua-sync push --dir src/locales --publish         # 导入后立即发布（受发布门禁约束）
```

- **目标项目必须已经在平台里存在**，`push` 不会创建项目（对不存在的项目服务端返回 404；脚本化接入可先调
  `POST /api/v1/projects/{项目}` 幂等创建，见 [README](README.md)）。
- 平台对导入不设密钥，`push` 不需要任何凭据（项目写密钥只用于服务端 SDK 的缺词上报）；能否推送取决于服务端是否可达。
- 顶层扁平 key 的归属命名空间由 `--default-ns` 决定，**默认 `common`**。平铺错误码这类要发布成
  JSON 根级裸 key 的词条，必须显式传 `--default-ns ''`（留空 = 项目根）——放进 `common` 会发布成嵌套对象，平铺 key 永远命不中。
- `--publish` 只在**最后一个**文件上生效（多语种时前面几个文件只导入）。
- 导入请求体上限 10MB（单个语种文件超限服务端返回 413），超大文件请拆分。
- 导入一律按「已审核」入库；占位符 / 长度 / HTML / 术语校验只做统计提示（导入结果里的 `flaggedIssues` 条数），
  `--publish` 时发布门禁拦截就只导入不发布，并输出 `publishBlockedReason`。
- 导入一个平台还没有的语种文件，会自动把这个语种加到项目的语种里（`LinguaCultures.Resolve` 校验，`en-us` / `zh_CN`
  这类写法会规范回已有代码，不会凭空造语种）。

## check：完成度与一致性检查

```bash
lingua-sync check                      # 检查本地 src/locales（先 pull 过）
lingua-sync check --remote             # 直接检查线上版本，不落盘
lingua-sync check --min-coverage 95    # 任一非默认语种完成度低于 95% 即失败
lingua-sync check --strict             # 有警告也算失败
```

做的事：

1. **完成度**：以默认语种为基准，统计每个语种缺了多少条。
2. **占位符一致性**：`{name}` / `{0}` / `{count, plural, …}` / `%s` 在译文里必须与源文案一致，不一致是**错误**。
3. **源码 key 扫描**（`--no-scan` 关闭）：扫 `src` 下 `.vue/.ts/.tsx/.js/.jsx/.mjs/.cjs/.cs/.razor/.cshtml`，
   识别 `$t('…')`、`t("…")`、`i18n.global.t(…)`、`L["…"]`、`Localizer["…"]` 等写法。
   代码里用到但默认语种里没有 → **错误**；默认语种里有但代码没引用 → 警告。
   模板字符串 `` `order.${status}` ``、拼接 `'order.' + x`、`$"order.{x}"` 会退化成前缀，避免误报；
   变量传 key（`t(keyVar)`）识别不到，用 `dynamicKeyPrefixes` 配前缀白名单。
4. **术语检查**：配了项目时默认会拉 `GET /api/v1/projects/{code}/glossary` 做检查（禁止翻译 / 标准译法 / 可接受变体 /
   禁用译法，与发布门禁同一套判定），违规算**错误**。连不上服务时跳过并提示；`--glossary` 强制检查（拉取失败即报错），
   `--no-glossary` 关闭。

输出形如：

```
[lingua-sync] 默认语种 zh-CN，共 3 个语种，扫描 42 个源文件，用到 87 个字面量 key
  en-US    完成度 100.0%  (101/101)  术语遵循 100% (12/12)
  ja-JP    完成度  98.0%  (99/101)  术语遵循  92% (11/12)
  警告 ja-JP 缺少 2 条译文（完成度 98.0%）：RoleNotExist、UserNotExist
[lingua-sync] 检查通过（1 个警告）。
```

## 验证过什么

对本地 Lingua 服务、在一个含 `src/App.vue` 的目录下实际运行：

- `pull` 写出三个语种文件和 `lingua.lock.json`；再次 `pull` 因 manifest 304 直接返回「已是最新版本」；
- `push` 导入成功并写库，`--default-ns ''` 的平铺 key 发布在 JSON 根级；
- `check` 在本地文件与 `--remote` 两种模式下都能给出完成度与 key 扫描结果。

`packages/lingua-vue/test/` 下有 `analyze.test.mjs` 与 `glossary.test.mjs` 覆盖检查逻辑的纯函数部分
（`node --test`）。**没有验证**：CI 里接真实仓库跑 `check` 的完整流水线。
