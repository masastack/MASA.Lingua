/**
 * lingua-sync check 的纯函数部分（不做任何 IO，便于测试）：
 * 展平 bundle、占位符比对、代码里的 key 扫描、完成度 / 缺失 / 未使用报告。
 */

import { checkGlossary } from './glossary.mjs';

/** 把嵌套 JSON 展平成 { 'order.submitBtn': '去结算 ({count})' }；扁平 JSON 原样返回。非字符串叶子（数组、数字）转成字符串。 */
export function flatten(obj, prefix = '', out = {}) {
  for (const [k, v] of Object.entries(obj ?? {})) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (v !== null && typeof v === 'object' && !Array.isArray(v)) {
      flatten(v, key, out);
    } else {
      out[key] = typeof v === 'string' ? v : JSON.stringify(v);
    }
  }
  return out;
}

/** 提取文案里的占位符名：{name}、{0}、{count, plural, ...} 里的 count，以及 %s / %d。返回排序去重后的数组。 */
export function placeholders(text) {
  const names = new Set();
  for (const m of String(text).matchAll(/\{\s*([A-Za-z0-9_.]+)\s*[,}]/g)) {
    names.add(m[1]);
  }
  for (const m of String(text).matchAll(/%[sd]/g)) {
    names.add(m[0]);
  }
  return [...names].sort();
}

const JS_EXT = new Set(['.vue', '.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs']);
const DOTNET_EXT = new Set(['.cs', '.razor', '.cshtml']);

export function sourceKind(ext) {
  const e = ext.toLowerCase();
  if (JS_EXT.has(e)) return 'js';
  if (DOTNET_EXT.has(e)) return 'dotnet';
  return null;
}

/**
 * 扫描一个源文件里用到的 i18n key。
 *  - 字面量 key：$t('order.submitBtn')、t("a.b")、i18n.global.t(...)、L["order.submitBtn"]、Localizer["…"]
 *  - 动态 key：模板字符串 `order.${status}`、字符串拼接 'order.' + status、$"order.{status}"
 *    只能得到前缀（order.），用来避免把这一类 key 误报为「未使用」。
 * 不会执行代码，也不是完整语法分析：变量里传 key（t(keyVar)）识别不到，需要在 dynamicKeyPrefixes 里配前缀白名单。
 */
export function scanSource(text, kind) {
  const literals = [];
  const dynamicPrefixes = [];
  const lineOf = (index) => text.slice(0, index).split('\n').length;

  if (kind === 'js') {
    const call = /(?<![\w$])(?:\$t[ce]?|t[ce]?|i18n\.(?:global\.)?t[ce]?)\(\s*(['"`])((?:\\.|(?!\1)[^\\])*)\1/g;
    for (const m of text.matchAll(call)) {
      const quote = m[1];
      const body = m[2];
      const after = text.slice(m.index + m[0].length).match(/^\s*(.)?/)?.[1] ?? '';
      const line = lineOf(m.index);

      if (quote === '`' && body.includes('${')) {
        dynamicPrefixes.push({ prefix: body.slice(0, body.indexOf('${')), line });
      } else if (after === '+') {
        dynamicPrefixes.push({ prefix: body, line });
      } else if (after === ')' || after === ',' || after === '') {
        literals.push({ key: body, line });
      }
    }
  } else if (kind === 'dotnet') {
    const literal = /(?<![\w])(?:L|Loc|Localizer|localizer|_localizer|_l|_loc)\s*\[\s*"((?:\\.|[^"\\])*)"/g;
    for (const m of text.matchAll(literal)) {
      literals.push({ key: m[1], line: lineOf(m.index) });
    }
    const interpolated = /(?<![\w])(?:L|Loc|Localizer|localizer|_localizer|_l|_loc)\s*\[\s*\$"([^"{]*)\{/g;
    for (const m of text.matchAll(interpolated)) {
      dynamicPrefixes.push({ prefix: m[1], line: lineOf(m.index) });
    }
  }

  return { literals, dynamicPrefixes };
}

/**
 * 生成检查报告。
 * @param bundles        { culture: 嵌套或扁平 JSON 对象 }
 * @param defaultCulture 源语种（其他语种以它为基准）
 * @param usage          可选；{ literals: Map<key, 'file:line'>, dynamicPrefixes: string[] }，来自 scanSource 汇总
 * @param options        { dynamicKeyPrefixes: string[]（白名单前缀，匹配的 key 不报未使用）, minCoverage: number }
 */
export function analyze({ bundles, defaultCulture, usage, options = {} }) {
  const errors = [];
  const warnings = [];
  const coverage = {};

  const flat = Object.fromEntries(Object.entries(bundles).map(([c, b]) => [c, flatten(b)]));
  const base = flat[defaultCulture];
  if (!base) {
    errors.push({ code: 'NO_DEFAULT_CULTURE', message: `没有找到默认语种 ${defaultCulture} 的内容。` });
    return { errors, warnings, coverage };
  }

  const baseKeys = Object.keys(base);
  for (const [culture, messages] of Object.entries(flat)) {
    if (culture === defaultCulture) {
      coverage[culture] = { total: baseKeys.length, translated: baseKeys.length, percent: 100 };
      continue;
    }

    let translated = 0;
    const missing = [];
    for (const key of baseKeys) {
      const value = messages[key];
      if (value === undefined || value.trim() === '') {
        missing.push(key);
        continue;
      }

      translated++;
      const want = placeholders(base[key]).join('|');
      const got = placeholders(value).join('|');
      if (want !== got) {
        errors.push({
          code: 'PLACEHOLDER_MISMATCH',
          culture,
          key,
          message: `${culture} 的 ${key} 占位符与 ${defaultCulture} 不一致（期望 {${want || '无'}}，实际 {${got || '无'}}）。`
        });
      }
    }

    const percent = baseKeys.length === 0 ? 100 : Math.round((translated * 1000) / baseKeys.length) / 10;
    coverage[culture] = { total: baseKeys.length, translated, percent, missing };
    if (missing.length > 0) {
      warnings.push({
        code: 'MISSING_TRANSLATION',
        culture,
        message: `${culture} 缺少 ${missing.length} 条译文（完成度 ${percent}%）：${missing.slice(0, 5).join('、')}${missing.length > 5 ? ' …' : ''}`
      });
    }

    const extra = Object.keys(messages).filter((k) => !(k in base));
    if (extra.length > 0) {
      warnings.push({
        code: 'EXTRA_KEYS',
        culture,
        message: `${culture} 有 ${extra.length} 条 key 在 ${defaultCulture} 中不存在：${extra.slice(0, 5).join('、')}${extra.length > 5 ? ' …' : ''}`
      });
    }
  }

  // 术语检查：与平台发布门禁同口径（术语违规一律阻断）
  let glossaryCompliance;
  if (options.glossary?.length) {
    const { violations, compliance } = checkGlossary(flat, defaultCulture, options.glossary);
    glossaryCompliance = compliance;
    for (const v of violations) {
      errors.push({ code: v.code, culture: v.culture, key: v.key, message: `${v.culture} 的 ${v.key}：${v.message}` });
    }
  }

  if (typeof options.minCoverage === 'number') {
    for (const [culture, c] of Object.entries(coverage)) {
      if (c.percent < options.minCoverage) {
        errors.push({
          code: 'COVERAGE_BELOW_MIN',
          culture,
          message: `${culture} 完成度 ${c.percent}% 低于要求的 ${options.minCoverage}%。`
        });
      }
    }
  }

  if (usage) {
    const whitelist = options.dynamicKeyPrefixes ?? [];
    const dynamic = [...usage.dynamicPrefixes, ...whitelist].filter((p) => p.length > 0);

    for (const [key, where] of usage.literals) {
      if (!(key in base)) {
        errors.push({ code: 'USED_BUT_MISSING', key, message: `代码里用到了 ${key}（${where}），但 ${defaultCulture} 中没有这条文案。` });
      }
    }

    const unused = baseKeys.filter((k) => !usage.literals.has(k) && !dynamic.some((p) => k.startsWith(p)));
    if (unused.length > 0) {
      warnings.push({
        code: 'UNUSED_KEYS',
        message: `${unused.length} 条文案在代码里没有找到引用：${unused.slice(0, 8).join('、')}${unused.length > 8 ? ' …' : ''}（动态 key 请在 dynamicKeyPrefixes 里配置前缀）`
      });
    }
  }

  return { errors, warnings, coverage, glossaryCompliance };
}
