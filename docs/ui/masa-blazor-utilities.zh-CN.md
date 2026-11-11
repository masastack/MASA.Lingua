# Masa.Blazor Utility Classes 权威参考

> 本文档面向 **人和 AI 检索**。所有数值、存在性结论均来自 `masa-blazor.min.css` 的机读提取，不凭 Vuetify 文档推断。
> 机器可读索引：[`masa-blazor-utilities.json`](masa-blazor-utilities.json)。

---

## §0 元信息与检索协议

### 0.1 适用范围

| 项 | 值 |
|---|---|
| 组件库 | Masa.Blazor |
| 提取版本 | `1.12.0-alpha.2`（见 JSON `source.packageVersion`） |
| 源文件 | `staticwebassets/css/masa-blazor.min.css`（582 KB / 6962 条规则 / 3733 个单类选择器） |
| 提取脚本 | [`build/extract-masa-utilities.mjs`](../../build/extract-masa-utilities.mjs) |
| 生成时间 | 见 JSON `generatedAt` |

### 0.2 如何核对与更新

```bash
# 重新提取（自动定位 NuGet 缓存里最高版本的 masa.blazor 包）
node build/extract-masa-utilities.mjs

# 或显式指定编译产物
node build/extract-masa-utilities.mjs path/to/masa-blazor.min.css
```

升级 Masa.Blazor 后**必须**重跑一次，再对照本文档的表格是否漂移。不要手改 JSON。

手工抽查单个类是否存在：

```bash
rg -o '\.m-application \.pa-4\{[^}]*\}' path/to/masa-blazor.min.css
```

### 0.3 AI 检索三原则

1. **先查存在性，再写类名。** 本文档 §3–§12 每节的第一张表就是存在性表；JSON 是唯一真值。
2. **再查皮肤等级。** 标 `B` 的类在换肤后会脱节，不要用于承载视觉。
3. **最后才考虑自写 CSS。** 只有在「Masa 不存在 + 组件属性不覆盖」时才允许进白名单（§14）。

### 0.4 Masa 不提供的族（**不要写**）

以下类名来自 Vuetify v3 的 styles 文档，Masa.Blazor 1.12 **一条规则都没有**。写了就是静默失效。

| 族 | Vuetify 参考 | Masa 规则数 | 改用 |
|---|---|---|---|
| `border` / `border-*` | [borders](https://v3.vuetifyjs.com/en/styles/borders/) | 0 | `MSheet Outlined`、`MDivider`、组件 `Outlined` 属性 |
| `cursor-*` | [cursor](https://v3.vuetifyjs.com/en/styles/cursor/) | 0 | 语义正确的 `<button>` / `<a>` |
| `opacity-*` | [opacity](https://v3.vuetifyjs.com/en/styles/opacity/) | 0 | `text--secondary` / `text--disabled` |
| `position-*` | [position](https://v3.vuetifyjs.com/en/styles/position/) | 0 | `MSticky`、`Fixed` 属性 |
| `w-*` `h-*` `min-w-*` `min-h-*` `max-w-*` `max-h-*` | [sizing](https://v3.vuetifyjs.com/en/styles/sizing/) | 0 | 组件 `Width` / `Height` / `MaxWidth` 属性、`fill-height` |
| `gap-*` `ga-*` `gr-*` | [spacing](https://v3.vuetifyjs.com/en/styles/spacing/) | 0 | `ma-*` / `pa-*`，或白名单几何类 |

另外这些**语义色工具类也不存在**（只有 Material 调色板 `red--text` 之类存在）：

```
primary--text  secondary--text  accent--text  error--text
warning--text  success--text    info--text    bg-primary  bg-surface
```

> 颜色请走 MASA 组件的 `Color` 属性（`Primary` / `Error` / `Success` / `Info` / `Warning` / `Grey`），
> 或 `text--primary` / `text--secondary` / `text--disabled`（§11）。

### 0.5 断点

| 断点 | 起 | 止 |
|---|---|---|
| `sm` | 600px | 959.98px |
| `md` | 960px | 1263.98px |
| `lg` | 1264px | 1903.98px |
| `xl` | 1904px | — |

响应式类的写法是 **`前缀-断点-值`**，断点在中间：

- ✅ `pa-md-4`、`d-md-none`、`flex-md-row`、`text-md-right`
- ❌ `pa-4-md`（不存在）

响应式类表示「该断点**及以上**」生效（与 Vuetify 一致），不是仅该区间。

---

## §1 使用决策树

写任何样式前，按顺序问：

```
① 这个属性是颜色 / 圆角 / 阴影 / 字体族吗？
   └─ 是 → 用 MASA 组件属性（Color / Rounded / Elevation / Dense）或 CSS 变量
           rgba(var(--m-theme-*))。不要用工具类：这些族全是字面值 + !important，
           换肤后不会跟随（见 §2）。

② 是间距 / 布局 / 显示 / 溢出 / 对齐吗？
   └─ 是 → 用 §3–§5、§8–§10 的 A 级工具类。

③ Masa 有对应类吗？
   └─ 无（§0.4 清单）→ 用组件属性；组件属性也覆盖不了 → 进白名单 CSS（§14），
                       只写几何，颜色一律 token。

④ 需要覆盖 .m-* 组件样式吗？
   └─ 是 → 停。这是皮肤包的职责，应用层不做。
```

---

## §2 皮肤兼容分级

MASA 的皮肤包（`masa-blazor-theme-adaptation`）是**纯 CSS RCL**：只覆盖 `--m-*` 令牌，作用域挂在 `html[data-skin="<id>"]` 或 `.theme--<id>-light/dark`，不碰应用类名。所以冲突的**唯一来源**是「绕过 token 写字面值」。

工具类因此分三级：

| 等级 | 含义 | 换肤后 | 代表类 |
|---|---|---|---|
| **A 安全** | 纯几何或布局属性，皮肤不参与该属性 | 完全正常 | 间距、display、flex、overflow、float、文本对齐 |
| **B 冲突** | 含字面值 + `!important`，皮肤覆盖不了 | 与皮肤脱节 | `text-h1..h6`、`display-*`、`elevation-*`、`rounded-*`、Material 调色板 |
| **C 例外安全** | 读 `var(--m-theme-*)` 通道令牌 | 跟随皮肤 | `text--primary` / `text--secondary` / `text--disabled` |

JSON 里每个类都带 `skinLevel` 字段，可直接按等级过滤检索。

### 2.1 三个典型切肤故障

**① 字体混排。** `text-h5` 的声明是：

```css
.m-application .text-h5 {
  font-size: 1.5rem !important;
  font-weight: 400;
  line-height: 2rem;
  letter-spacing: normal !important;
  font-family: Roboto, sans-serif !important;   /* ← 皮肤赢不了 */
}
```

多套皮肤会在 `.m-application` 上挂自己的 `--<design>-font-sans`（如 `.theme--element-light .m-application { font-family: var(--element-font-sans); }`），但组件级的 `font-family: Roboto !important` 优先级更高。结果：换成 Element 皮肤后，标题仍是 Roboto，正文是 Element 字体。

**② 阴影不跟随。** `elevation-2` 写死 `rgba(0,0,0,.2)/.14/.12` 三层阴影 + `!important`，皮肤的 `--<design>-shadow-*` 无法生效。

**③ 圆角不跟随。** `rounded-lg` 写死 `8px !important`。皮肤若把圆角刻度改成 4/6/10，工具类仍是 8。

### 2.2 规避方式

- 视觉（颜色 / 圆角 / 阴影 / 字体族）→ 组件属性或 CSS 变量
- 排版尺度（字号 / 行高）→ 若必须跟随皮肤，用语义标签 + 白名单字号，或接受 MASA 固定尺度
- A 级工具类可以放心用，`!important` 在这里无害，因为皮肤不声明这些属性

---

## §3 Spacing（间距）

`检索键`：间距 内边距 外边距 padding margin pa ma px mx mt mb ms me gap

### 3.1 存在性表

| 项 | 值 |
|---|---|
| 类总数 | 459（base）+ 1836（响应式） |
| 属性前缀 | `m`（margin）、`p`（padding） |
| 方向后缀 | `a` 全部、`t` 上、`b` 下、`l` 左、`r` 右、`x` 左右、`y` 上下、`s` 行首、`e` 行尾 |
| 尺寸 | `0`–`16`（4px 基数）、`auto`（仅 margin）、`n1`–`n16`（负值，仅 margin） |
| 响应式 | 有，`-sm/-md/-lg/-xl` |
| 皮肤等级 | **A** |

### 3.2 尺寸对照（4px 基数）

| 后缀 | 值 | 后缀 | 值 |
|---|---|---|---|
| `0` | 0 | `9` | 36px |
| `1` | 4px | `10` | 40px |
| `2` | 8px | `11` | 44px |
| `3` | 12px | `12` | 48px |
| `4` | 16px | `13` | 52px |
| `5` | 20px | `14` | 56px |
| `6` | 24px | `15` | 60px |
| `7` | 28px | `16` | 64px |
| `8` | 32px | `auto` | auto（仅 margin） |

负值：`ma-n1` = `margin: -4px !important` … `ma-n16` = `-64px`。**只有 margin 有负值**。

### 3.3 精确声明（抽样，全部见 JSON）

```css
.m-application .pa-0  { padding: 0 !important }
.m-application .pa-4  { padding: 16px !important }
.m-application .pa-6  { padding: 24px !important }
.m-application .px-4  { padding-right: 16px !important; padding-left: 16px !important }
.m-application .ma-n4 { margin: -16px !important }
.m-application .mx-auto { margin-right: auto !important; margin-left: auto !important }
```

### 3.4 逻辑方向 `s` / `e`

`ms-*` / `me-*` **不**依赖 `.m-application` 作用域，而是挂在方向类上：

```css
.m-application--is-ltr .ms-4 { margin-left: 16px !important }
.m-application--is-ltr .me-4 { margin-right: 16px !important }
.m-application--is-rtl .ms-4 { margin-right: 16px !important }
.m-application--is-rtl .me-4 { margin-left: 16px !important }
```

LTR 环境下 `ms` = left、`me` = right，与 `ml` / `mr` 等价。**新代码优先用 `ml` / `mr`**：语义直白，且不依赖方向类存在。

### 3.5 响应式

```razor
@* 手机 16px、md 起 24px *@
<div class="pa-4 pa-md-6">…</div>
```

断点写在中间，`pa-4-md` 这种写法**不存在**。

### 3.6 关于 gap

Masa **没有** `gap-*`。flex 子项间距两条路：

1. 给子项加 `mr-2` / `mb-2`（简单场景）
2. 白名单几何类（本项目用 `.lingua-stack` / `.lingua-toolbar`，见 §14）

---

## §4 Display（显示）

`检索键`：显示 隐藏 display d-flex d-none d-block d-inline d-table sr-only print

### 4.1 存在性表

| 项 | 值 |
|---|---|
| Base 类 | 10 个 |
| 响应式变体 | 45 个（`sm` / `md` / `lg` / `xl` / `print`） |
| 皮肤等级 | **A**（`d-sr-only` 也是 A） |

### 4.2 基础值

| 类 | 声明 |
|---|---|
| `d-none` | `display: none !important` |
| `d-inline` | `display: inline !important` |
| `d-inline-block` | `display: inline-block !important` |
| `d-block` | `display: block !important` |
| `d-table` | `display: table !important` |
| `d-table-row` | `display: table-row !important` |
| `d-table-cell` | `display: table-cell !important` |
| `d-flex` | `display: flex !important` |
| `d-inline-flex` | `display: inline-flex !important` |
| `d-sr-only` | 见 4.4 |

### 4.3 响应式与打印

```razor
@* 手机上隐藏，md 及以上显示 *@
<aside class="d-none d-md-block">…</aside>

@* 打印时隐藏 *@
<div class="d-print-none">…</div>
```

响应式值集合与基础值相同（9 个，无 `d-sr-only`）；`print` 前缀同样 9 个。

### 4.4 `d-sr-only`（屏幕阅读器专用）

```css
.d-sr-only, .d-sr-only-focusable:not(:focus) {
  border: 0 !important;
  clip: rect(0,0,0,0) !important;
  height: 1px !important;
  margin: -1px !important;
  overflow: hidden !important;
  padding: 0 !important;
  position: absolute !important;
  white-space: nowrap !important;
  width: 1px !important;
}
```

**这个类是全局作用域的**（前面没有 `.m-application`），所以在 `<MApp>` 之外也能用。它就是自写 `.sr-only` 的标准替代品——本项目原先在 `app-structure.css` 里手写了一份，已删除。

配对的 `d-sr-only-focusable` 在聚焦时恢复可见，用于「跳到主内容」这类链接。

---

## §5 Flex 与布局

`检索键`：弹性 布局 flex justify align order spacer fill-height grow shrink

### 5.1 存在性表

| 项 | 值 |
|---|---|
| Base 类 | 54 个 |
| 响应式变体 | 196 个 |
| 皮肤等级 | **A** |

### 5.2 方向与伸缩

| 类 | 声明 |
|---|---|
| `flex-row` / `flex-row-reverse` | `flex-direction: row / row-reverse` |
| `flex-column` / `flex-column-reverse` | `flex-direction: column / column-reverse` |
| `flex-wrap` / `flex-nowrap` / `flex-wrap-reverse` | `flex-wrap: …` |
| `flex-fill` | `flex: 1 1 auto` |
| `flex` | `flex: 1 1 auto; max-width: 100%` |
| `flex-grow-0` / `flex-grow-1` | `flex-grow: 0 / 1` |
| `flex-shrink-0` / `flex-shrink-1` | `flex-shrink: 0 / 1` |

> `flex` 与 `flex-fill` 的差别：前者额外限 `max-width:100%`。表格单元格里想撑满但别溢出，用 `flex`。

### 5.3 主轴对齐 `justify-*`

| 类 | 声明 |
|---|---|
| `justify-start` | `justify-content: flex-start` |
| `justify-end` | `justify-content: flex-end` |
| `justify-center` | `justify-content: center` |
| `justify-space-between` | `justify-content: space-between` |
| `justify-space-around` | `justify-content: space-around` |

### 5.4 交叉轴对齐

| 类 | 声明 |
|---|---|
| `align-start` / `-end` / `-center` / `-baseline` / `-stretch` | `align-items: …` |
| `align-content-start` / `-end` / `-center` / `-space-between` / `-space-around` / `-stretch` | `align-content: …` |
| `align-self-auto` / `-start` / `-end` / `-center` / `-baseline` / `-stretch` | `align-self: …` |

### 5.5 顺序 `order-*`

`order-0` … `order-12`、`order-first`（`-1`）、`order-last`（`13`）。

### 5.6 全局辅助类

| 类 | 声明 | 作用域 |
|---|---|---|
| `spacer` | `flex-grow: 1 !important` | 全局 |
| `grow` | `flex-grow: 1; flex-shrink: 0` | 全局 |
| `shrink` | `flex-grow: 0; flex-shrink: 1` | 全局 |
| `fill-height` | `height: 100%` | 全局 |

`spacer` 在 MASA 里同时有组件形态 `<MSpacer />`，两者等价；模板里优先用组件（可读性更好）。

### 5.7 响应式

格式 `前缀-断点-值`：

```razor
@* 手机纵向、md 起横向 *@
<div class="d-flex flex-column flex-md-row">…</div>

@* 手机左对齐、lg 起右对齐 *@
<div class="d-flex justify-start justify-lg-end">…</div>
```

覆盖族：`flex-*`（方向/换行/增长/收缩）、`justify-*`、`align-*`、`align-content-*`、`align-self-*`、`order-*`。

---

## §6 Border Radius（圆角）

`检索键`：圆角 radius rounded

### 6.1 存在性表

| 项 | 值 |
|---|---|
| Base 类 | 7 个（无方向） |
| 方向变体 | 56 个 |
| 响应式 | **无** |
| 皮肤等级 | **B**（字面像素 + `!important`，皮肤覆盖不了） |

### 6.2 刻度

| 类 | 声明 |
|---|---|
| `rounded-0` | `border-radius: 0` |
| `rounded-sm` | `border-radius: 2px` |
| `rounded` | `border-radius: 4px` |
| `rounded-lg` | `border-radius: 8px` |
| `rounded-xl` | `border-radius: 24px` |
| `rounded-pill` | `border-radius: 9999px` |
| `rounded-circle` | `border-radius: 50%` |

### 6.3 方向变体

8 个方向 × 7 个刻度 = 56 个：`rounded-{t|b|l|r|tl|tr|bl|br}` 与 `rounded-{方向}-{0|sm|lg|xl|pill|circle}`。

```css
.m-application .rounded-t-lg { border-top-left-radius: 8px !important; border-top-right-radius: 8px !important }
```

### 6.4 为什么标 B 级

皮肤可能通过 `--<design>-radius-*` 重新定义圆角策略（例如 Element 用 4px、Material3 用 12px）。工具类写死像素 + `!important`，切肤后会出现「卡片跟皮肤、标签还是 8px」的混搭。

**首选**：`MCard Rounded`、`MSheet Rounded`、`MTextField Rounded` 等组件属性——它们最终映射到皮肤自己的几何规则。

**次选**：白名单 CSS 用 `border-radius`，但接受它是本项目自己的刻度。

---

## §7 Elevation（阴影）

`检索键`：阴影 海拔 elevation shadow 投影

### 7.1 存在性表

| 项 | 值 |
|---|---|
| 类 | `elevation-0` … `elevation-24`，共 25 个 |
| 响应式 | **无** |
| 皮肤等级 | **B** |

### 7.2 声明形态

三层阴影叠加，值写死 rgba（不读 token）：

```css
.m-application .elevation-0 {
  box-shadow: 0 0 0 0 rgba(0,0,0,.2), 0 0 0 0 rgba(0,0,0,.14), 0 0 0 0 rgba(0,0,0,.12) !important;
}
.m-application .elevation-2 {
  box-shadow: 0 3px 1px -2px rgba(0,0,0,.2),
              0 2px 2px 0   rgba(0,0,0,.14),
              0 1px 5px 0   rgba(0,0,0,.12) !important;
}
```

完整 0–24 见 JSON 的 `families[elevation].classes[].declaration`。

### 7.3 使用约定

| 场景 | 做法 |
|---|---|
| 静止的卡片 / 面板 | **不加阴影**。用 `MSheet` 的 tone step（表面比底色深一档）或 1px 描边 |
| 菜单 / 弹窗 / Tooltip / 抽屉 | 用组件自身 `Elevation` 属性，不要手写 `elevation-*` |
| 确实需要强调浮层 | `Elevation` 属性取值 4–8，避免 16 以上 |

`elevation-*` 是 B 级：皮肤若用 `--<design>-shadow-*` 定义阴影，工具类会让该处脱离皮肤。

---

## §8 Overflow（溢出）

`检索键`：溢出 滚动 overflow scroll

### 8.1 存在性表

| 项 | 值 |
|---|---|
| 类 | 9 个 |
| 响应式 | **无** |
| 皮肤等级 | **A** |

### 8.2 全部类

| 类 | 声明 |
|---|---|
| `overflow-auto` | `overflow: auto` |
| `overflow-hidden` | `overflow: hidden` |
| `overflow-visible` | `overflow: visible` |
| `overflow-x-auto` / `-hidden` / `-visible` | `overflow-x: …` |
| `overflow-y-auto` / `-hidden` / `-visible` | `overflow-y: …` |

### 8.3 标准组合

横向滚动表格（宽表 + 固定列）：

```razor
<div class="overflow-x-auto">
    <MSimpleTable Dense>…</MSimpleTable>
</div>
```

抽屉内容区（自身滚动、footer 贴底）：

```razor
<div class="d-flex flex-column fill-height">
    <div class="px-4 pb-4 flex-grow-1 overflow-y-auto">@ChildContent</div>
    <div class="px-4 py-3">@FooterContent</div>
</div>
```

---

## §9 Float（浮动）

`检索键`：浮动 float

| 项 | 值 |
|---|---|
| Base 类 | 5 个 |
| 响应式 + print | 25 个 |
| 皮肤等级 | **A** |

| 类 | 声明 |
|---|---|
| `float-none` | `float: none` |
| `float-left` | `float: left` |
| `float-right` | `float: right` |
| `float-start` / `float-end` | 按 LTR/RTL 映射到 left/right |

响应式写法 `float-md-right`；打印 `float-print-none`。

**新代码优先 flex。** 浮动唯一合理的残留场景：正文内图片环绕。管理后台的布局一律用 flex + `spacer`。

---

## §10 Content / Typography（内容与排版）

`检索键`：排版 字号 字体 typography text-h1 text-body caption 标题 字距 行高

### 10.1 存在性表

| 项 | 值 |
|---|---|
| 排版类 | 26 个（`text-*` 与旧别名各一套，二者声明完全相同） |
| 响应式 | 每类 4 个断点变体 |
| 皮肤等级 | **B**（内嵌 `font-family: Roboto !important`） |

### 10.2 尺度表（`text-*` 形式）

| 类 | 字号 | 字重 | 行高 | 字距 |
|---|---|---|---|---|
| `text-h1` | 6rem (96px) | 300 | 6rem | -0.015625em |
| `text-h2` | 3.75rem (60px) | 300 | 3.75rem | -0.0083333em |
| `text-h3` | 3rem (48px) | 400 | 3.125rem | normal |
| `text-h4` | 2.125rem (34px) | 400 | 2.5rem | 0.0073529em |
| `text-h5` | 1.5rem (24px) | 400 | 2rem | normal |
| `text-h6` | 1.25rem (20px) | 500 | 2rem | 0.0125em |
| `text-subtitle-1` | 1rem (16px) | 400 | 1.75rem | 0.009375em |
| `text-subtitle-2` | 0.875rem (14px) | 500 | 1.375rem | 0.0071429em |
| `text-body-1` | 1rem (16px) | 400 | 1.5rem | 0.03125em |
| `text-body-2` | 0.875rem (14px) | 400 | 1.25rem | 0.0178571em |
| `text-button` | 0.875rem (14px) | 500 | 2.25rem | 0.0892857em + 大写 |
| `text-caption` | 0.75rem (12px) | 400 | 1.25rem | 0.0333333em |
| `text-overline` | 0.75rem (12px) | 500 | 2rem | 0.1666667em + 大写 |
| `display-1` | 2.125rem | 400 | 2.5rem | 0.0073529em |
| `display-4` | 6rem | 300 | 6rem | -0.015625em |
| `headline` | 1.5rem | 400 | 2rem | normal |
| `title` | 1.25rem | 500 | 2rem | 0.0125em |
| `blockquote` | 18px | 300 | — | `padding: 16px 0 16px 24px` |

旧别名（`headline` / `title` / `subtitle-1` / `body-1` / `caption` / `overline`）与 `text-*` 形式**声明逐字相同**，新代码统一用 `text-*`。

每类都可加断点前缀：`text-md-h5`、`text-lg-body-1` 等。

### 10.3 所有排版类都带 `font-family: Roboto, sans-serif !important`

这是 B 级的根因，也是换肤后「字体混排」的来源。三个应对：

1. **接受**：内部管理后台统一用 MASA 排版尺度，皮肤只换颜色不换字体——最简单。
2. **皮肤层修正**：由皮肤包自己再写一条同特异度规则（`.theme--x .text-h5 { font-family: inherit }`）——属于皮肤职责，应用层不做。
3. **不用排版类**：改用语义标签（`<h1>`–`<h6>`）+ 本项目白名单字号。代价是丢掉 MASA 的排版一致性。

本项目采取方案 1。

### 10.4 文本对齐与处理

| 类 | 声明 |
|---|---|
| `text-left` / `text-right` / `text-center` / `text-justify` | `text-align: …` |
| `text-start` / `text-end` | `text-align: start / end`（随方向） |
| `text-truncate` | `white-space: nowrap; overflow: hidden; text-overflow: ellipsis` |
| `text-no-wrap` | `white-space: nowrap` |
| `text-wrap` | `white-space: normal` |
| `text-break` | `overflow-wrap: break-word; word-break: break-word` |
| `text-pre` / `text-pre-line` / `text-pre-wrap` | `white-space: pre / pre-line / pre-wrap` |
| `text-uppercase` / `text-lowercase` / `text-capitalize` / `text-none` | `text-transform: …` |
| `text-decoration-underline` / `-overline` / `-line-through` / `-none` | `text-decoration: …` |
| `font-weight-thin`(100) / `-light`(300) / `-regular`(400) / `-medium`(500) / `-bold`(700) / `-black`(900) | `font-weight: …` |
| `font-italic` | `font-style: italic` |

> **注意**：Vuetify 的 `text-nowrap` 在 Masa 里叫 **`text-no-wrap`**。`text-nowrap` 不存在，写了不生效。

对齐类有响应式变体（`text-md-right` 等）；`text-truncate` / `text-no-wrap` / `font-weight-*` **没有**响应式变体。

---

## §11 语义色与强调

`检索键`：颜色 强调 次级 禁用 emphasis text--secondary text--disabled Color 属性

### 11.1 存在性表

| 类 | 声明 | 皮肤等级 |
|---|---|---|
| `text--primary` | `color: rgba(var(--m-theme-on-surface))` | **C** |
| `text--secondary` | `color: rgba(var(--m-theme-on-surface), var(--m-medium-emphasis-opacity))` | **C** |
| `text--disabled` | `color: rgba(var(--m-theme-on-surface), var(--m-disabled-opacity))` | **C** |

这三个是**唯一推荐的文字着色工具类**：它们读通道令牌，换肤自动跟随。

### 11.2 不存在的语义色工具类

```
primary--text   secondary--text   accent--text
error--text     warning--text     success--text    info--text
bg-primary      bg-surface
```

Masa 里只有 **Material 调色板**的固定色（`red--text`、`grey lighten-2` 等，见 JSON 的 `palette` 族）。它们写死 Material 色值、**与主题无关**，换肤不跟随，因此**不要用于语义**。

| 用法 | 结果 |
|---|---|
| `class="red--text"` 表示「错误」 | 皮肤换成绿色主题时仍是 Material 红——语义与主题脱节 |
| `<MChip Color="Error">` | 映射到 `--m-theme-error`，皮肤可覆盖 ✅ |

### 11.3 正确做法：用组件 `Color` 属性

```razor
<MChip Color="Warning">待复核</MChip>
<MButton Color="Primary">发布</MButton>
<MAlert Type="@AlertTypes.Error">…</MAlert>
<MIcon Color="Success">mdi-circle</MIcon>
```

合法值：`Primary` / `Secondary` / `Accent` / `Error` / `Info` / `Success` / `Warning` / `Grey` 及 Material 色名。

组件 `Color` 最终映射到 `--m-theme-*` 通道，皮肤可覆盖。

### 11.4 需要自定义颜色时的唯一写法

白名单 CSS 里：

```css
.lingua-something {
  color: rgb(var(--m-theme-info));
  background: rgba(var(--m-theme-on-surface), var(--m-border-opacity));
  border-color: rgb(var(--m-theme-primary));
}
```

永远不写 `#4318ff`、`rgb(67,24,255)` 这类字面值。

---

## §12 Transition（过渡）

`检索键`：过渡 动画 transition ease

| 类 | 声明 |
|---|---|
| `transition-fast-out-slow-in` | `transition: .3s cubic-bezier(.4,0,.2,1)` |
| `transition-linear-out-slow-in` | `transition: .3s cubic-bezier(0,0,.2,1)` |
| `transition-fast-out-linear-in` | `transition: .3s cubic-bezier(.4,0,1,1)` |
| `transition-ease-in-out` | `transition: .3s cubic-bezier(.4,0,.6,1)` |
| `transition-fast-in-fast-out` | `transition: .3s cubic-bezier(.25,.8,.25,1)` |
| `transition-swing` | `transition: .3s cubic-bezier(.25,.8,.5,1)` |

皮肤等级 **A**。但必须配 `@media (prefers-reduced-motion: reduce)` 关闭动画——本项目已在 `app-structure.css` 末尾全局处理。

---

## §13 反模式

| 反模式 | 为什么错 | 正确做法 |
|---|---|---|
| 在应用层写 `.m-btn { … }` | 越权覆盖组件，皮肤失效 | 用组件属性 |
| `!important` 覆盖组件样式 | 同上，且造成优先级军备竞赛 | 用 `@layer` 或组件属性 |
| 引用 `--m-internal-*` | 保留前缀，随时可改 | 只用 `--m-theme-*` / `--m-*-opacity` |
| 颜色写 `#4318ff` | 换肤不跟随 | `rgb(var(--m-theme-primary))` |
| 引用 `--sk-*` / `--ms-*` / `--masa-design-*` | 皮肤私有变量 | 同上 |
| 用 `text-h5` 只为调字号 | 顺带锁死字体族与字距（B 级） | 接受，或在白名单里单写 `font-size` |
| `rounded-lg` 做视觉圆角 | B 级，皮肤不跟随 | 组件 `Rounded` 属性 |
| 同一属性既写工具类又写白名单 CSS | 后者被 `!important` 压过 | 只留一处 |
| 写 `w-100` / `gap-4` / `border-sm` | Masa 不存在，静默失效 | 见 §0.4 |

---

## §14 本项目白名单对照

`app-structure.css` 是应用层**唯一**允许的自有 CSS。每个类必须能回答「为什么工具类和组件属性都做不到」。

| 类 | 存在理由 | 是否含颜色 |
|---|---|---|
| `code` / `pre` | 键名、哈希、语种代码的等宽字体（数据标识符，非品牌样式） | 否 |
| `pre.code` | JSON 预览块：滚动 + 最大高度 + 描边 | 是（token） |
| `.lingua-matrix td` | 表格单元格垂直居中（`MSimpleTable` 无此属性） | 否 |
| `.lingua-matrix th.ai-col` / `td.ai-col` | 粘性 AI 列：`position: sticky` + 背景（Masa 无 `position-*`） | 是（token） |
| `.cell` / `.cell-input` | 原生 `<input>` 的编辑形态；键击级性能优化依赖原生 input，不能用 `MTextField` | 是（token） |
| `.cell-actions` / `.cell-notes` | 单元格内「保存 / 撤销」条与备注的缩进几何 | 否 |
| `.ctx-inline` / `.ctx-input` | 「上下文与约束」副行（虚线→实线） | 是（token） |
| `.boot-splash` / `.ai-spinner` | 电路建立前的静态占位（此时主题变量还没注入） | 是（带功能性回退） |
| `.lingua-stack` / `.lingua-stack-tight` | flex column + gap（Masa 无 `gap-*`） | 否 |
| `.lingua-toolbar` | flex wrap + 水平/垂直 gap（同上） | 否 |
| `.lingua-shell` | 内容栏线：`max-width: 1560px`（Masa 无 sizing 工具类） | 否 |
| `.lingua-page-head` | 页头两列：标题在左、操作在右，窄屏换行 | 否 |
| `.lingua-metrics` / `.lingua-metric` | 分隔式指标条（app-screens §5 的 KPI 形态） | 是（token） |
| `.lingua-bar` | 堆叠完成度条的几何（高度/圆角/裁剪） | 否 |
| `.lingua-num` | `font-variant-numeric: tabular-nums`（数字对齐） | 否 |
| `.lingua-drawer` / `-head` / `-body` / `-foot` | 抽屉三段式：头部固定、body 滚动、footer 贴底 | 否 |

### 新增白名单类的门槛

三条全过才能加：

1. 不含颜色字面值（颜色只能 `rgb(var(--m-theme-*))` 或 `rgba(var(--m-theme-on-surface), var(--m-*-opacity))`）
2. 不覆盖任何 `.m-*` 组件选择器
3. 不使用 `!important`

不满足 → 说明应该用组件属性或工具类。

### 已删除的类

| 类 | 原因 |
|---|---|
| `.sr-only` | 内置 `d-sr-only` 等价，且是全局作用域 |
| `.zero` | 从未有规则（`LocalTime.razor` 里的死类） |
| `.key` / `.act` | 从未有规则（表格单元格上的死类） |

---

## §15 附录

### 15.1 JSON 结构

```jsonc
{
  "generatedBy": "build/extract-masa-utilities.mjs",
  "generatedAt": "…",
  "source": {
    "file": "1.12.0-alpha.2/staticwebassets/css/masa-blazor.min.css",
    "locatedVia": "nuget-global-packages",
    "packageVersion": "1.12.0-alpha.2",
    "bytes": 582377,
    "ruleCount": 6962,
    "singleClassSelectorCount": 3733
  },
  "breakpoints": [ { "key": "sm", "minWidth": 600, "maxWidth": 959.98 } ],
  "skinLevels": { "A": "…", "B": "…", "C": "…" },
  "families": [
    {
      "id": "spacing",
      "title": "Spacing（间距）",
      "keywords": ["间距", "pa", "ma"],
      "classCount": 459,
      "classes": [
        { "class": "pa-6", "kind": "base", "skinLevel": "A", "declaration": "padding:24px!important" },
        { "class": "pa-md-6", "kind": "responsive", "skinLevel": "A",
          "responsive": { "md": ["padding:24px!important"] } },
        { "class": "ms-4", "kind": "base", "skinLevel": "A",
          "declarations": ["margin-left:16px!important", "margin-right:16px!important"],
          "scope": ["ltr", "rtl"] }
      ]
    }
  ],
  "absentFamilies": [
    { "id": "borders", "title": "Borders（边框）", "exists": false, "alternative": "…" }
  ],
  "absentColorClasses": [ { "class": "primary--text", "exists": false } ],
  "globalUtilityClasses": ["d-sr-only", "fill-height", "spacer"]
}
```

字段说明：

| 字段 | 含义 |
|---|---|
| `kind` | `base`（无断点）/ `responsive`（仅断点下生效）/ `directional`（方向变体，如 `rounded-t-lg`） |
| `skinLevel` | `A` / `B` / `C`，见 §2 |
| `declaration` | 单条声明原文（含 `!important`） |
| `declarations` | 多作用域时的全部声明（如 `ms-4` 的 LTR + RTL 两份） |
| `scope` | 仅当不是 `.m-application` 作用域时出现 |
| `responsive` | 断点 → 声明列表 |

### 15.2 与 Vuetify v3 同名文档的差异汇总

| Vuetify | Masa.Blazor 1.12 | 说明 |
|---|---|---|
| [Border radius](https://v3.vuetifyjs.com/en/styles/border-radius/) | 有，63 个类 | 刻度相同，但标 B 级 |
| [Borders](https://v3.vuetifyjs.com/en/styles/borders/) | **无** | 用 `MSheet Outlined` 等 |
| [Content](https://v3.vuetifyjs.com/en/styles/content/#block-quote) | 有（`blockquote` / `text-*` / `display-*`） | 多了 `font-family: Roboto !important` |
| [Cursor](https://v3.vuetifyjs.com/en/styles/cursor/) | **无** | — |
| [Display](https://v3.vuetifyjs.com/en/styles/display/) | 有，55 个 | 多了 `-print-` 前缀 |
| [Elevation](https://v3.vuetifyjs.com/en/styles/elevation/) | 有，25 个 | 无响应式变体 |
| [Flex](https://v3.vuetifyjs.com/en/styles/flex/) | 有，250 个 | 与 Vuetify 结构一致 |
| [Float](https://v3.vuetifyjs.com/en/styles/float/) | 有，30 个 | 与 Vuetify 一致 |
| [Opacity](https://v3.vuetifyjs.com/en/styles/opacity/) | **无** | 用 `text--secondary` |
| [Overflow](https://v3.vuetifyjs.com/en/styles/overflow/) | 有，9 个 | 无响应式变体 |
| [Position](https://v3.vuetifyjs.com/en/styles/position/) | **无** | 用 `MSticky` |
| [Sizing](https://v3.vuetifyjs.com/en/styles/sizing/) | **无** | 用组件宽高属性 |
| [Spacing](https://v3.vuetifyjs.com/en/styles/spacing/) | 有，459 base + 1836 响应式 | 无 `gap-*` |
| （Vuetify 无） | `text--primary/secondary/disabled` | Masa 特有，C 级，推荐 |

### 15.3 相关文档

- 皮肤契约（`masa-blazor-theme-adaptation` 仓库）：`docs/design/SKIN-CONTRACT.md`
- 项目集成指南：[`docs/integration/aspnetcore.md`](../integration/aspnetcore.md#masa-blazor-接入)
