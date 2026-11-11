#!/usr/bin/env node
/*
 * 从 Masa.Blazor 的编译产物 masa-blazor.min.css 里提取「工具类真值」，产出
 * docs/ui/masa-blazor-utilities.json，供 docs/ui/masa-blazor-utilities.zh-CN.md 引用，
 * 也供 AI 直接检索（避免凭 Vuetify 记忆写出 Masa 不存在的类）。
 *
 * 用法：
 *   node build/extract-masa-utilities.mjs                     # 自动定位 NuGet 缓存里的 CSS
 *   node build/extract-masa-utilities.mjs <path/to/min.css>    # 指定文件
 *
 * 定位顺序：命令行参数 → NuGet 全局包缓存（masa.blazor/<最高版本>/staticwebassets/css/）→ 仓库 bin 输出。
 *
 * 为什么不在文档里手抄：Masa.Blazor 升级后工具类会漂移，手抄必然说谎。这里把「存在性 + 精确声明 +
 * 响应式变体 + 作用域」全部机读化，升级后重跑一次即可；文档正文只解释怎么用，数值一律引用本 JSON。
 */

import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const OUT_JSON = path.join(repoRoot, 'docs', 'ui', 'masa-blazor-utilities.json');

/** Masa.Blazor 的断点（与 Masa/Vuetify 的 sm/md/lg/xl 一一对应，取自编译产物里的 @media）。 */
const BREAKPOINTS = [
  { key: 'sm', minWidth: 600, maxWidth: 959.98 },
  { key: 'md', minWidth: 960, maxWidth: 1263.98 },
  { key: 'lg', minWidth: 1264, maxWidth: 1903.98 },
  { key: 'xl', minWidth: 1904, maxWidth: null },
];

// ---------------------------------------------------------------------------
// 定位 CSS
// ---------------------------------------------------------------------------

function nugetGlobalPackages() {
  try {
    const output = execFileSync('dotnet', ['nuget', 'locals', 'global-packages', '--list'], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
    });
    const match = output.match(/global-packages:\s*(.+)/i);
    return match ? match[1].trim() : null;
  } catch {
    return null;
  }
}

function compareVersions(a, b) {
  const pa = a.split(/[.-]/);
  const pb = b.split(/[.-]/);
  for (let i = 0; i < Math.max(pa.length, pb.length); i += 1) {
    const na = Number.parseInt(pa[i] ?? '0', 10);
    const nb = Number.parseInt(pb[i] ?? '0', 10);
    if (Number.isNaN(na) || Number.isNaN(nb)) {
      const sa = pa[i] ?? '';
      const sb = pb[i] ?? '';
      if (sa !== sb) return sa < sb ? -1 : 1;
      continue;
    }
    if (na !== nb) return na - nb;
  }
  return 0;
}

function locateCss() {
  if (process.argv[2]) {
    const explicit = path.resolve(process.argv[2]);
    if (!fs.existsSync(explicit)) throw new Error(`指定的 CSS 不存在：${explicit}`);
    return { file: explicit, source: 'argument' };
  }

  const globalPackages = nugetGlobalPackages();
  if (globalPackages) {
    const pkgDir = path.join(globalPackages, 'masa.blazor');
    if (fs.existsSync(pkgDir)) {
      const versions = fs
        .readdirSync(pkgDir, { withFileTypes: true })
        .filter((e) => e.isDirectory())
        .map((e) => e.name)
        .sort(compareVersions)
        .reverse();
      for (const version of versions) {
        const candidate = path.join(pkgDir, version, 'staticwebassets', 'css', 'masa-blazor.min.css');
        if (fs.existsSync(candidate)) return { file: candidate, source: 'nuget-global-packages', version };
      }
    }
  }

  // 兜底：仓库里任何一次构建留下的副本（含测试宿主的临时目录不参与）
  const fallback = path.join(
    repoRoot,
    'src',
    'MASA.Lingua.Server',
    'bin',
    'Debug',
    'net10.0',
    'wwwroot',
    '_content',
    'Masa.Blazor',
    'css',
    'masa-blazor.min.css',
  );
  if (fs.existsSync(fallback)) return { file: fallback, source: 'build-output', version: null };

  throw new Error(
    '找不到 masa-blazor.min.css。请先 dotnet restore（让 NuGet 缓存里有 Masa.Blazor 包），或显式传路径：\n'
      + '  node build/extract-masa-utilities.mjs <path/to/masa-blazor.min.css>',
  );
}

// ---------------------------------------------------------------------------
// 解析 CSS
// ---------------------------------------------------------------------------

/**
 * 把编译产物摊平成 { selector, declarations, atRule } 列表。
 * min.css 是单行、无嵌套，用「花括号配对 + 记录最近的 @media」即可，不需要完整 CSS 解析器。
 */
function flattenRules(css) {
  const rules = [];
  const atStack = [];
  let buffer = '';
  let depth = 0;

  for (let i = 0; i < css.length; i += 1) {
    const ch = css[i];

    if (ch === '{') {
      const head = buffer.trim();
      buffer = '';
      if (head.startsWith('@media')) {
        atStack.push(head);
        continue;
      }
      depth += 1;
      // 找到匹配的右花括号（编译产物里没有嵌套规则，一层即可）
      let j = i + 1;
      let inner = 0;
      for (; j < css.length; j += 1) {
        if (css[j] === '{') inner += 1;
        else if (css[j] === '}') {
          if (inner === 0) break;
          inner -= 1;
        }
      }
      const body = css.slice(i + 1, j);
      rules.push({ selector: head, declarations: body.trim(), atRule: atStack.at(-1) ?? null });
      i = j;
      depth = 0;
    } else if (ch === '}') {
      atStack.pop();
      buffer = '';
    } else {
      buffer += ch;
    }
  }

  return rules;
}

function mediaOf(atRule) {
  if (!atRule) return null;
  for (const bp of BREAKPOINTS) {
    const minHit = new RegExp(`min-width:\\s*${bp.minWidth}px`).test(atRule);
    const maxHit = bp.maxWidth
      ? new RegExp(`max-width:\\s*${bp.maxWidth}px`).test(atRule)
      : false;
    if (minHit && (!bp.maxWidth || maxHit)) return bp.key;
    if (minHit) return bp.key; // only min-width given → 该断点及以上
  }
  if (/print/.test(atRule)) return 'print';
  return 'other';
}

/**
 * Masa 的工具类有四套作用域：
 *   .m-application <cls>            —— 只在 MApp 内部生效（绝大多数）
 *   .m-application--is-ltr <cls>    —— 逻辑方向（ms/me/float start/end）
 *   .m-application--is-rtl <cls>
 *   <cls>                            —— 极少数全局类（d-sr-only、spacer、fill-height）
 */
function classifySelector(selectorPart) {
  // 保留伪类（如 .d-sr-only-focusable:not(:focus) 只在未聚焦时生效，语义不同），
  // 但允许类名后带伪类尾巴，否则这类规则会整条漏掉。
  const trimmed = selectorPart.trim();
  const withPseudo = trimmed.match(
    /^(?:\.m-application(?:--is-(?:ltr|rtl))?\s+)?\.([A-Za-z0-9_-]+)((?:::?[a-zA-Z-]+(?:\([^)]*\))?)*)$/,
  );
  if (withPseudo) {
    const [, className, pseudo] = withPseudo;
    const prefix = trimmed.slice(0, trimmed.indexOf(`.${className}`));
    let scope = 'global';
    if (prefix.includes('.m-application--is-ltr')) scope = 'ltr';
    else if (prefix.includes('.m-application--is-rtl')) scope = 'rtl';
    else if (prefix.includes('.m-application')) scope = 'app';
    return { scope, className, pseudo: pseudo || null };
  }
  return null;
}

// ---------------------------------------------------------------------------
// 工具类族定义（只收 Masa 实际存在的；不存在的族根本不进 JSON）
// ---------------------------------------------------------------------------

const FAMILIES = [
  {
    id: 'spacing',
    title: 'Spacing（间距）',
    keywords: ['间距', '内边距', '外边距', 'padding', 'margin', 'pa', 'ma', 'px', 'mx', 'mt', 'mb'],
    match: /^([mp])(a|t|b|l|r|x|y|s|e)-(n?\d{1,2}|auto)$/,
    // 响应式写法是「属性方向-断点-尺寸」，断点在中间：pa-md-4（pa-4-md 不存在）
    matchResponsive: /^([mp])(a|t|b|l|r|x|y|s|e)-(sm|md|lg|xl)-(n?\d{1,2}|auto)$/,
  },
  {
    id: 'display',
    title: 'Display（显示）',
    keywords: ['显示', '隐藏', 'display', 'd-flex', 'd-none', 'd-block', 'd-sm', 'd-md', 'sr-only'],
    match: /^d-(none|inline|inline-block|block|table|table-row|table-cell|flex|inline-flex|sr-only|sr-only-focusable)$/,
    matchResponsive: /^d-(sm|md|lg|xl|print)-(none|inline|inline-block|block|table|table-row|table-cell|flex|inline-flex)$/,
  },
  {
    id: 'flex',
    title: 'Flex 与布局',
    keywords: ['弹性', '布局', 'flex', 'justify', 'align', 'order', 'spacer', 'fill-height', 'grow', 'shrink'],
    match:
      /^(flex|flex-fill|flex-grow-[01]|flex-shrink-[01]|flex-(row|row-reverse|column|column-reverse|wrap|nowrap|wrap-reverse)|justify-(start|end|center|space-between|space-around)|align-(start|end|center|baseline|stretch)|align-content-(start|end|center|space-between|space-around|stretch)|align-self-(auto|start|end|center|baseline|stretch)|order-(first|last|\d{1,2})|spacer|grow|shrink|fill-height)$/,
    // 响应式格式是「前缀-断点-值」：flex-md-row / justify-md-center / align-self-md-center / order-md-first
    matchResponsive:
      /^(flex|justify|align|align-content|align-self|order)-(sm|md|lg|xl)-[a-z0-9]+(-[a-z0-9]+)*$/,
  },
  {
    id: 'border-radius',
    title: 'Border Radius（圆角）',
    keywords: ['圆角', 'radius', 'rounded', '圆'],
    match: /^rounded(-(0|sm|lg|xl|pill|circle))?$/,
    // 方向变体是「rounded-方向[-尺寸]」：rounded-t / rounded-t-lg / rounded-tl-xl
    matchDirectional: /^rounded-(t|b|l|r|tl|tr|bl|br)(-(0|sm|lg|xl|pill|circle))?$/,
    matchResponsive: /^rounded-(sm|md|lg|xl)-(t|b|l|r|tl|tr|bl|br)?-?(0|sm|lg|xl|pill|circle)?$/,
  },
  {
    id: 'elevation',
    title: 'Elevation（阴影）',
    keywords: ['阴影', '海拔', 'elevation', 'shadow', '投影'],
    match: /^elevation-(\d{1,2})$/,
    matchResponsive: /^elevation-(sm|md|lg|xl)-(\d{1,2})$/,
  },
  {
    id: 'overflow',
    title: 'Overflow（溢出）',
    keywords: ['溢出', '滚动', 'overflow', 'scroll', '滚动条'],
    match: /^overflow(-(x|y))?-(auto|hidden|visible)$/,
    matchResponsive: /^overflow-(sm|md|lg|xl)-((x|y)-)?(auto|hidden|visible)$/,
  },
  {
    id: 'float',
    title: 'Float（浮动）',
    keywords: ['浮动', 'float', '左浮', '右浮'],
    match: /^float-(none|left|right|start|end)$/,
    matchResponsive: /^float-(sm|md|lg|xl|print)-(none|left|right|start|end)$/,
  },
  {
    id: 'typography',
    title: 'Content / Typography（内容与排版）',
    keywords: ['排版', '字号', '字体', 'typography', 'text-h1', 'text-body', 'caption', '标题'],
    match:
      /^(text-(h[1-6]|subtitle-[12]|body-[12]|button|caption|overline)|display-[1-4]|headline|title|subtitle-[12]|body-[12]|caption|overline|blockquote)$/,
    matchResponsive: /^text-(sm|md|lg|xl)-(h[1-6]|subtitle-[12]|body-[12]|button|caption|overline)$/,
  },
  {
    id: 'text-alignment',
    title: 'Text Alignment 与文本处理',
    keywords: ['对齐', '换行', '截断', 'text-right', 'text-center', 'truncate', 'nowrap', 'uppercase'],
    match:
      /^(text-(left|right|center|justify|start|end)|text-(wrap|no-wrap|truncate|break|pre|pre-line|pre-wrap)|text-(uppercase|lowercase|capitalize|none)|text-decoration-(none|underline|overline|line-through)|font-(italic|weight-(thin|light|regular|medium|bold|black)))$/,
    matchResponsive: /^text-(sm|md|lg|xl)-(left|right|center|justify|start|end)$/,
  },
  {
    id: 'emphasis',
    title: '语义强调色（token 驱动）',
    keywords: ['颜色', '强调', '次级', '禁用', 'emphasis', 'text--secondary', 'text--disabled'],
    match: /^text--(primary|secondary|disabled)$/,
  },
  {
    id: 'transition',
    title: 'Transition（过渡）',
    keywords: ['过渡', '动画', 'transition', 'ease'],
    match:
      /^transition-(fast-out-slow-in|linear-out-slow-in|fast-out-linear-in|ease-in-out|fast-in-fast-out|swing)$/,
  },
  {
    // Material 调色板：存在，但写死 Material 色值、与主题无关。
    // 收进 JSON 只为「查得到 + 明确标注不可用于语义」，不是推荐用法。
    id: 'palette',
    title: 'Material 调色板（固定色，非语义）',
    keywords: ['调色板', 'palette', 'red', 'grey', 'material', '固定色'],
    match:
      /^(red|pink|purple|deep-purple|indigo|blue|light-blue|cyan|teal|green|light-green|lime|yellow|amber|orange|deep-orange|brown|blue-grey|grey|black|white|transparent)(--text)?$/,
  },
];

/**
 * 皮肤兼容等级。
 *   A —— 纯几何/布局，皮肤不参与该属性，`!important` 无害。
 *   B —— 含字面值 + `!important`（字体族、阴影、圆角、Material 调色板），皮肤无法覆盖。
 *   C —— 读 `var(--m-theme-*)`，跟随皮肤（唯一推荐的着色方式）。
 */
function skinLevel(familyId, className, declarations) {
  if (familyId === 'emphasis') return 'C';
  if (familyId === 'typography') return 'B';
  if (familyId === 'border-radius') return 'B';
  if (familyId === 'elevation') return 'B';
  if (familyId === 'palette') return 'B';
  if (/font-family\s*:/.test(declarations)) return 'B';
  if (/var\(--m-color-/.test(declarations)) return 'B';
  if (/var\(--m-theme-/.test(declarations)) return 'C';
  return 'A';
}

// ---------------------------------------------------------------------------
// 主流程
// ---------------------------------------------------------------------------

const { file, source, version } = locateCss();
const css = fs.readFileSync(file, 'utf8');
const rules = flattenRules(css);

/** className → { scope → { base: [...], responsive: { sm: [...], ... } } } */
const index = new Map();

for (const rule of rules) {
  const media = mediaOf(rule.atRule);
  for (const part of rule.selector.split(',')) {
    const hit = classifySelector(part);
    if (!hit) continue;
    const { scope, className } = hit;
    if (!index.has(className)) index.set(className, new Map());
    const byScope = index.get(className);
    if (!byScope.has(scope)) byScope.set(scope, { base: [], responsive: {} });
    const bucket = byScope.get(scope);
    const entry = { declarations: rule.declarations, media: media };
    if (media === null) bucket.base.push(entry);
    else {
      if (!bucket.responsive[media]) bucket.responsive[media] = [];
      bucket.responsive[media].push(entry);
    }
  }
}

function collectFamily(family) {
  const classes = [];
  for (const [className, byScope] of index) {
    const isBase = family.match?.test(className) ?? false;
    const isResponsive = family.matchResponsive?.test(className) ?? false;
    const isDirectional = family.matchDirectional?.test(className) ?? false;
    if (!isBase && !isResponsive && !isDirectional) continue;

    // 压缩表示：绝大多数类是 app 作用域 + base 单条声明，直接拍平；
    // 只有逻辑方向（ms/me）与全局类（d-sr-only）才保留 scope 信息。
    const scopeNames = [...byScope.keys()];
    const onlyApp = scopeNames.length === 1 && scopeNames[0] === 'app';
    const baseDecls = scopeNames.flatMap((s) => byScope.get(s).base.map((e) => e.declarations));
    const responsiveDecls = {};
    for (const s of scopeNames) {
      for (const [media, list] of Object.entries(byScope.get(s).responsive)) {
        responsiveDecls[media] = list.map((e) => e.declarations);
      }
    }

    const combined = [...baseDecls, ...Object.values(responsiveDecls).flat()].join(';');
    const entry = {
      class: className,
      kind: isResponsive ? 'responsive' : isDirectional ? 'directional' : 'base',
      skinLevel: skinLevel(family.id, className, combined),
    };
    if (baseDecls.length === 1) entry.declaration = baseDecls[0];
    else if (baseDecls.length > 1) entry.declarations = baseDecls;
    if (Object.keys(responsiveDecls).length > 0) entry.responsive = responsiveDecls;
    if (!onlyApp) entry.scope = scopeNames.length === 1 ? scopeNames[0] : scopeNames;
    classes.push(entry);
  }
  classes.sort((a, b) => a.class.localeCompare(b.class, 'en', { numeric: true }));
  return classes;
}

const families = FAMILIES.map((family) => {
  const classes = collectFamily(family);
  return {
    id: family.id,
    title: family.title,
    keywords: family.keywords,
    classCount: classes.length,
    classes,
  };
}).filter((f) => f.classCount > 0);

/**
 * 明确「Masa 不提供」的族。这些名字来自 Vuetify v3 的 styles 文档目录。
 * 判定方式：在整个编译产物里搜索该类名前缀，规则数为 0 才算不存在。
 */
const ABSENT_FAMILIES = [
  {
    id: 'borders',
    title: 'Borders（边框）',
    vuetifyUrl: 'https://v3.vuetifyjs.com/en/styles/borders/',
    probe: /^\.border[a-z0-9-]*$/,
    alternative: 'MSheet Outlined / MDivider / 组件 Outlined 属性；需要自定义线色时用 rgba(var(--m-theme-on-surface), var(--m-border-opacity))',
  },
  {
    id: 'cursor',
    title: 'Cursor（光标）',
    vuetifyUrl: 'https://v3.vuetifyjs.com/en/styles/cursor/',
    probe: /^\.cursor-[a-z-]+$/,
    alternative: '使用语义正确的 <button> / <a>（浏览器自带光标），或白名单 CSS 的 cursor 几何规则',
  },
  {
    id: 'opacity',
    title: 'Opacity（不透明度）',
    vuetifyUrl: 'https://v3.vuetifyjs.com/en/styles/opacity/',
    probe: /^\.opacity-[a-z0-9-]+$/,
    alternative: 'text--secondary / text--disabled，或 rgba(var(--m-theme-on-surface), var(--m-low-emphasis-opacity))',
  },
  {
    id: 'position',
    title: 'Position（定位）',
    vuetifyUrl: 'https://v3.vuetifyjs.com/en/styles/position/',
    probe: /^\.position-[a-z-]+$/,
    alternative: 'MSticky 组件、MNavigationDrawer 的 Fixed 属性，或白名单 CSS（纯几何）',
  },
  {
    id: 'sizing',
    title: 'Sizing（尺寸 w-/h-/min-/max-）',
    vuetifyUrl: 'https://v3.vuetifyjs.com/en/styles/sizing/',
    probe: /^\.(w|h|min-w|min-h|max-w|max-h)-[a-z0-9-]+$/,
    alternative: '组件 Width / Height / MaxWidth 属性、fill-height，或白名单 CSS',
  },
  {
    id: 'gap',
    title: 'Gap（网格间距）',
    vuetifyUrl: 'https://v3.vuetifyjs.com/en/styles/spacing/',
    probe: /^\.(gap|ga|gr|row-gap|column-gap)-[a-z0-9-]+$/,
    alternative: '间距用 ma-/pa- 系列；flex 子项间距用白名单几何类（本项目 .lingua-stack / .lingua-toolbar）',
  },
];

const allClassNames = [...index.keys()];const absent = ABSENT_FAMILIES.map((f) => ({
  id: f.id,
  title: f.title,
  vuetifyUrl: f.vuetifyUrl,
  exists: allClassNames.some((c) => f.probe.test(`.${c}`)),
  alternative: f.alternative,
}));

/** Vuetify 有、Masa 也没有的语义色工具类（容易凭记忆写错）。 */
const ABSENT_COLOR_CLASSES = [
  'primary--text',
  'secondary--text',
  'accent--text',
  'error--text',
  'warning--text',
  'success--text',
  'info--text',
  'bg-primary',
  'bg-surface',
].map((cls) => ({ class: cls, exists: index.has(cls) }));

/**
 * 把 CSS 路径归一化成「包内相对路径 + 版本」，避免把本机的绝对路径写进仓库文件
 * （NuGet 缓存路径里有临时目录与随机段，提交上去既不可读也无意义）。
 */
function describeSource(filePath, locatedVia) {
  const normalized = filePath.replaceAll('\\', '/');
  const m = normalized.match(/masa\.blazor\/([^/]+)\/staticwebassets\/css\/masa-blazor\.min\.css$/i);
  if (m) {
    return {
      file: `${m[1]}/staticwebassets/css/masa-blazor.min.css`,
      locatedVia,
      packageVersion: m[1],
    };
  }
  return {
    file: path.relative(repoRoot, filePath).replaceAll('\\', '/'),
    locatedVia,
    packageVersion: null,
  };
}

const payload = {
  generatedBy: 'build/extract-masa-utilities.mjs',
  generatedAt: new Date().toISOString(),
  source: {
    ...describeSource(file, source),
    bytes: Buffer.byteLength(css, 'utf8'),
    ruleCount: rules.length,
    singleClassSelectorCount: allClassNames.length,
  },
  breakpoints: BREAKPOINTS,
  skinLevels: {
    A: '纯几何或布局属性，皮肤不参与，!important 无害',
    B: '含字面值 + !important（字体族 / 阴影 / 圆角 / Material 调色板），皮肤覆盖不了，切肤会脱节',
    C: '读 var(--m-theme-*) 通道令牌，跟随皮肤，唯一推荐的着色方式',
  },
  families,
  absentFamilies: absent,
  absentColorClasses: ABSENT_COLOR_CLASSES,
  /**
   * 真正可用的「全局类」：不带 .m- 前缀、不带过渡/布局内部名。
   * 编译产物里绝大多数 .m-* 是组件内部类，不该被应用层直接使用；过渡类由组件自己加。
   */
  globalUtilityClasses: [...index.entries()]
    .filter(([className, byScope]) => byScope.has('global'))
    .map(([className]) => className)
    .filter(
      (c) =>
        !c.startsWith('m-')
        && !c.startsWith('p-')
        && !c.startsWith('pMobile')
        && !/(transition|enter|leave|move|active|to)$/.test(c)
        && !/^(col|row|container|layout|flex|grow|shrink)/.test(c),
    )
    .sort(),
};

fs.mkdirSync(path.dirname(OUT_JSON), { recursive: true });
fs.writeFileSync(OUT_JSON, `${JSON.stringify(payload, null, 2)}\n`, 'utf8');

const total = families.reduce((n, f) => n + f.classCount, 0);
console.log(`CSS: ${payload.source.file} (${payload.source.ruleCount} 规则, ${payload.source.singleClassSelectorCount} 个单类选择器)`);
console.log(`工具类族: ${families.length} 个，${total} 个类`);
for (const f of families) console.log(`  - ${f.id.padEnd(16)} ${String(f.classCount).padStart(4)}  ${f.title}`);
for (const a of absent) console.log(`  x ${a.id.padEnd(16)} ${a.exists ? '存在（未删除）' : '不存在'}`);
console.log(`输出: ${path.relative(repoRoot, OUT_JSON)}`);
