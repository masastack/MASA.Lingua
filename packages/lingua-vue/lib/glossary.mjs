/**
 * 术语检查（纯函数，不做 IO）：与服务端 GlossaryMatcher / GlossaryRules 保持同一套判定，
 * 这样 CI 里 `lingua-sync check` 的结论与平台里的校验、发布门禁一致。
 *
 * 术语的形状来自 GET /api/v1/projects/{code}/glossary：
 *   { sourceTerm, doNotTranslate, caseSensitive, targets: {culture: text}, acceptedVariants: {culture: [..]}, forbidden: {culture: [..]} }
 */

export const GLOSSARY_RULES = {
  DNT: 'GLOSSARY_DNT_VIOLATION',
  MISMATCH: 'GLOSSARY_TERM_MISMATCH',
  FORBIDDEN: 'GLOSSARY_FORBIDDEN_TERM'
};

const isLatinWordChar = (ch) => ch !== undefined && ch < '\u0250' && /[\p{L}\p{N}_]/u.test(ch);

function indexOfTerm(text, term, from, caseSensitive) {
  return caseSensitive
    ? text.indexOf(term, from)
    : text.toLowerCase().indexOf(term.toLowerCase(), from);
}

/** 拉丁字母 / 数字构成的边界必须是词边界；中日韩按子串匹配。返回所有命中区间。 */
export function findSpans(text, term, caseSensitive) {
  const spans = [];
  if (!text || !term) return spans;
  let index = 0;
  while (index <= text.length - term.length && (index = indexOfTerm(text, term, index, caseSensitive)) >= 0) {
    const end = index + term.length;
    const leftBad = isLatinWordChar(term[0]) && index > 0 && isLatinWordChar(text[index - 1]);
    const rightBad = isLatinWordChar(term[term.length - 1]) && end < text.length && isLatinWordChar(text[end]);
    if (!leftBad && !rightBad) spans.push([index, end]);
    index++;
  }
  return spans;
}

export function containsWord(text, word, caseSensitive) {
  const w = (word ?? '').trim();
  if (!text || !w) return false;
  return findSpans(text, w, caseSensitive).length > 0;
}

/** 源文案命中的术语（最长匹配优先，重叠即去重：「结算中心」覆盖的位置，较短的「结算」不再重复命中，等长重叠同理）。保持输入顺序。 */
export function findMatchedTerms(sourceText, terms) {
  if (!sourceText || !terms?.length) return [];
  const candidates = [];
  terms.forEach((term, order) => {
    const text = (term.sourceTerm ?? '').trim();
    if (!text) return;
    const spans = findSpans(sourceText, text, !!term.caseSensitive);
    if (spans.length > 0) candidates.push({ term, order, spans, length: text.length });
  });
  if (candidates.length <= 1) return candidates.map((c) => c.term);

  const claimed = [];
  const matched = [];
  for (const c of [...candidates].sort((a, b) => b.length - a.length)) {
    const free = c.spans.filter(
      ([s, e]) => !claimed.some(([ks, ke]) => s < ke && ks < e)
    );
    if (free.length === 0) continue;
    claimed.push(...free);
    matched.push(c);
  }
  return matched.sort((a, b) => a.order - b.order).map((m) => m.term);
}

function lookup(dict, culture) {
  if (!dict) return undefined;
  const k = Object.keys(dict).find((x) => x.toLowerCase() === culture.toLowerCase());
  return k === undefined ? undefined : dict[k];
}

/** 一个术语对一条译文的判定：{ applies, violations: [{ code, severity, message, suggestedFix }] }。 */
export function evaluateTerm(term, culture, translated) {
  const source = (term.sourceTerm ?? '').trim();
  const caseSensitive = !!term.caseSensitive;
  const has = (needle) =>
    caseSensitive ? translated.includes(needle) : translated.toLowerCase().includes(needle.toLowerCase());
  const violations = [];

  if (term.doNotTranslate) {
    // 与服务端同口径：按词边界（"LinguaPhone" 不算保留了 "Lingua"），中日韩按子串
    if (!containsWord(translated, source, caseSensitive)) {
      violations.push({
        code: GLOSSARY_RULES.DNT,
        severity: 'error',
        message: `专有术语 "${source}" 标记为禁止翻译，但译文中未原样保留。`,
        suggestedFix: source
      });
    }
    return { applies: true, violations };
  }

  let applies = false;
  const expected = lookup(term.targets, culture);
  const variants = lookup(term.acceptedVariants, culture) ?? [];
  const forbidden = lookup(term.forbidden, culture) ?? [];

  if (expected && expected.trim()) {
    applies = true;
    const followed = has(expected) || variants.some((v) => v && v.trim() && has(v));
    if (!followed) {
      const alt = variants.length > 0 ? `（可接受变体：${variants.join('、')}）` : '';
      violations.push({
        code: GLOSSARY_RULES.MISMATCH,
        severity: 'warning',
        message: `源文案包含术语 "${source}"，建议在 [${culture}] 中使用标准译法 "${expected}"${alt}。`,
        suggestedFix: expected
      });
    }
  }

  if (forbidden.length > 0) {
    applies = true;
    for (const word of forbidden.filter((w) => w && w.trim())) {
      if (containsWord(translated, word, caseSensitive)) {
        const hint = expected && expected.trim() ? `，请使用 "${expected}"` : '';
        violations.push({
          code: GLOSSARY_RULES.FORBIDDEN,
          severity: 'error',
          message: `术语 "${source}" 在 [${culture}] 中禁止译为 "${word.trim()}"${hint}。`,
          suggestedFix: expected && expected.trim() ? expected : null
        });
      }
    }
  }

  return { applies, violations };
}

/**
 * 对所有语种做术语检查。
 * @param flat           { culture: { key: text } }（已展平）
 * @param defaultCulture 源语种；源语种是术语的来源，不检查
 * @param terms          术语数组
 * @returns { violations: [{ culture, key, code, severity, message }], compliance: { culture: { checked, followed, percent } } }
 */
export function checkGlossary(flat, defaultCulture, terms) {
  const violations = [];
  const compliance = {};
  const base = flat[defaultCulture] ?? {};
  if (!terms?.length) return { violations, compliance };

  const matchedByKey = new Map(Object.entries(base).map(([key, text]) => [key, findMatchedTerms(text, terms)]));

  for (const [culture, messages] of Object.entries(flat)) {
    if (culture === defaultCulture) continue;
    let checked = 0;
    let followed = 0;
    for (const [key, matched] of matchedByKey) {
      const text = messages[key];
      if (!matched.length || text === undefined || text.trim() === '') continue; // 缺失属于完成度问题
      for (const term of matched) {
        const r = evaluateTerm(term, culture, text);
        if (!r.applies) continue;
        checked++;
        if (r.violations.length === 0) {
          followed++;
        } else {
          for (const v of r.violations) violations.push({ culture, key, ...v });
        }
      }
    }
    compliance[culture] = {
      checked,
      followed,
      percent: checked === 0 ? null : Math.floor((followed * 100) / checked)
    };
  }

  return { violations, compliance };
}
