import assert from 'node:assert/strict';
import http from 'node:http';
import { test } from 'node:test';
import { analyze } from '../lib/analyze.mjs';
import { check, fetchGlossary } from '../lib/cli.mjs';
import { checkGlossary, containsWord, evaluateTerm, findMatchedTerms, GLOSSARY_RULES } from '../lib/glossary.mjs';

const term = (over) => ({ sourceTerm: '工作台', doNotTranslate: false, caseSensitive: false, targets: {}, acceptedVariants: {}, forbidden: {}, ...over });

test('evaluateTerm：标准译法或可接受变体都算遵循，否则提示并带出变体', () => {
  const t = term({ targets: { 'en-US': 'Workspace' }, acceptedVariants: { 'en-US': ['Work space'] } });
  assert.equal(evaluateTerm(t, 'en-US', 'Open the Workspace').violations.length, 0);
  assert.equal(evaluateTerm(t, 'en-US', 'open the work space').violations.length, 0);

  const miss = evaluateTerm(t, 'en-US', 'Open the dashboard');
  assert.equal(miss.violations[0].code, GLOSSARY_RULES.MISMATCH);
  assert.match(miss.violations[0].message, /Work space/);
  assert.equal(evaluateTerm(t, 'ja-JP', 'なんでも').applies, false);
});

test('evaluateTerm：禁用译法出现即违规，按词边界，不误伤 Workspace', () => {
  const t = term({ targets: { 'en-US': 'Workspace' }, forbidden: { 'en-US': ['Dashboard', 'Work'] } });
  const both = evaluateTerm(t, 'en-US', 'Workspace dashboard');
  assert.equal(both.violations.length, 1);
  assert.equal(both.violations[0].code, GLOSSARY_RULES.FORBIDDEN);
  assert.equal(both.violations[0].suggestedFix, 'Workspace');

  assert.equal(evaluateTerm(t, 'en-US', 'Open Workspace').violations.length, 0);
  assert.equal(evaluateTerm(t, 'en-US', 'Open Workspace to Work').violations.length, 1);
  assert.equal(containsWord('Workspace', 'Work', false), false);
  assert.equal(containsWord('提交代码', '提交', false), true); // CJK 子串
});

test('evaluateTerm：禁止翻译必须原样保留，大小写策略生效', () => {
  const dnt = term({ sourceTerm: 'Lingua', doNotTranslate: true, caseSensitive: true });
  assert.equal(evaluateTerm(dnt, 'en-US', 'Welcome to Lingua').violations.length, 0);
  assert.equal(evaluateTerm(dnt, 'en-US', 'Welcome to lingua').violations[0].code, GLOSSARY_RULES.DNT);
});

test('evaluateTerm：禁止翻译按词边界，子串不算保留', () => {
  const dnt = term({ sourceTerm: 'Lingua', doNotTranslate: true });
  assert.equal(evaluateTerm(dnt, 'en-US', 'Welcome to Lingua').violations.length, 0);
  assert.equal(evaluateTerm(dnt, 'en-US', 'welcome to lingua').violations.length, 0); // 忽略大小写时小写也算
  assert.equal(evaluateTerm(dnt, 'en-US', 'Welcome to LinguaPhone').violations[0].code, GLOSSARY_RULES.DNT);
});

test('findMatchedTerms：等长重叠只命中先声明的一条（中日韩按子串命中）', () => {
  const terms = [term({ sourceTerm: '工作台设' }), term({ sourceTerm: '台设置' })];
  assert.deepEqual(findMatchedTerms('工作台设置', terms).map((t) => t.sourceTerm), ['工作台设']);
});

test('findMatchedTerms：最长匹配优先、拉丁词按词边界', () => {
  const terms = [term({ sourceTerm: '结算' }), term({ sourceTerm: '结算中心' }), term({ sourceTerm: 'Lingua' })];
  assert.deepEqual(findMatchedTerms('进入结算中心', terms).map((t) => t.sourceTerm), ['结算中心']);
  assert.deepEqual(findMatchedTerms('结算中心与结算', terms).map((t) => t.sourceTerm), ['结算', '结算中心']);
  assert.deepEqual(findMatchedTerms('Linguaphone', terms), []);
});

test('checkGlossary：只统计命中且有译文的受约束术语，源语种不检查', () => {
  const terms = [term({ targets: { 'en-US': 'Workspace' }, forbidden: { 'en-US': ['Dashboard'] } })];
  const flat = {
    'zh-CN': { a: '打开工作台', b: '工作台设置', c: '返回工作台', d: '确定' },
    'en-US': { a: 'Open Workspace', b: 'Dashboard settings', d: 'OK' }
  };
  const { violations, compliance } = checkGlossary(flat, 'zh-CN', terms);
  assert.equal(compliance['zh-CN'], undefined);
  assert.deepEqual(compliance['en-US'], { checked: 2, followed: 1, percent: 50 });
  assert.ok(violations.every((v) => v.key === 'b' && v.culture === 'en-US'));
  assert.ok(violations.some((v) => v.code === GLOSSARY_RULES.FORBIDDEN));
});

test('analyze：术语违规作为错误上报，并带上遵循率', () => {
  const report = analyze({
    bundles: { 'zh-CN': { home: { title: '打开工作台' } }, 'en-US': { home: { title: 'Open Dashboard' } } },
    defaultCulture: 'zh-CN',
    options: { glossary: [term({ targets: { 'en-US': 'Workspace' }, forbidden: { 'en-US': ['Dashboard'] } })] }
  });
  assert.ok(report.errors.some((e) => e.code === GLOSSARY_RULES.FORBIDDEN && e.key === 'home.title'));
  assert.equal(report.glossaryCompliance['en-US'].percent, 0);

  const clean = analyze({
    bundles: { 'zh-CN': { home: { title: '打开工作台' } }, 'en-US': { home: { title: 'Open Workspace' } } },
    defaultCulture: 'zh-CN',
    options: { glossary: [term({ targets: { 'en-US': 'Workspace' } })] }
  });
  assert.equal(clean.errors.length, 0);
});

test('check --remote：从平台拉取生效术语并检查；服务不可用时默认跳过，--glossary 则报错', async (t) => {
  const seen = [];
  const glossary = { project: 'p', terms: [term({ targets: { 'en-US': 'Workspace' }, forbidden: { 'en-US': ['Dashboard'] } })] };
  const manifest = { defaultCulture: 'zh-CN', cultures: [{ culture: 'zh-CN', url: '/i18n/p/zh-CN.json' }, { culture: 'en-US', url: '/i18n/p/en-US.json' }] };
  const server = http.createServer((req, res) => {
    seen.push({ url: req.url });
    const send = (body) => { res.setHeader('content-type', 'application/json'); res.end(JSON.stringify(body)); };
    if (req.url === '/api/v1/projects/p/glossary') return send(glossary);
    if (req.url === '/api/v1/projects/p/manifest') return send(manifest);
    if (req.url === '/i18n/p/zh-CN.json') return send({ home: { title: '打开工作台' } });
    if (req.url === '/i18n/p/en-US.json') return send({ home: { title: 'Open Dashboard' } });
    res.statusCode = 404; res.end('{}');
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  t.after(() => server.close());
  const endpoint = `http://127.0.0.1:${server.address().port}`;

  const logs = [];
  const cfg = { endpoint, project: 'p', cultures: null, defaultCulture: null, dynamicKeyPrefixes: [], src: '', out: '' };
  const result = await check(cfg, { remote: 'true', 'no-scan': 'true' }, (m) => logs.push(m));
  assert.equal(result.failed, true);
  assert.ok(result.errors.some((e) => e.code === GLOSSARY_RULES.FORBIDDEN));
  assert.ok(logs.some((l) => l.includes('术语遵循')));
  assert.ok(seen.some((s) => s.url.endsWith('/glossary')));

  // --no-glossary：不请求术语端点，也就没有术语错误
  const off = await check(cfg, { remote: 'true', 'no-scan': 'true', 'no-glossary': 'true' }, () => {});
  assert.equal(off.errors.length, 0);

  // 连不上：默认跳过，显式要求则报错
  const dead = { ...cfg, endpoint: 'http://127.0.0.1:1' };
  assert.equal(await fetchGlossary(dead, {}, () => {}), null);
  await assert.rejects(() => fetchGlossary(dead, { glossary: 'true' }, () => {}));
});
