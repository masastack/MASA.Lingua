import assert from 'node:assert/strict';
import { test } from 'node:test';
import { analyze, flatten, placeholders, scanSource } from '../lib/analyze.mjs';
import { parseArgs } from '../lib/cli.mjs';

test('flatten 展平嵌套 JSON，扁平 JSON 原样保留', () => {
  assert.deepEqual(flatten({ order: { submitBtn: 'A', tip: { x: 'B' } }, 'common.ok': 'OK' }), {
    'order.submitBtn': 'A',
    'order.tip.x': 'B',
    'common.ok': 'OK'
  });
});

test('placeholders 识别 {name}、ICU 与 %s', () => {
  assert.deepEqual(placeholders('去结算 ({count}) {name}'), ['count', 'name']);
  assert.deepEqual(placeholders('{n, plural, one {# item} other {# items}}'), ['n']);
  assert.deepEqual(placeholders('共 %d 项 %s'), ['%d', '%s']);
  assert.deepEqual(placeholders('没有占位符'), []);
});

test('scanSource 识别 Vue 字面量 / 动态 key，忽略 get( 之类的误匹配', () => {
  const code = `
    <template>{{ $t('order.submitBtn') }} {{ t("common.ok", { n: 1 }) }}</template>
    <script setup>
    const label = i18n.global.t('order.title');
    const status = t(\`order.status.\${s}\`);
    const dyn = t('order.type.' + type);
    const noise = get('not.a.key') + split('x.y');
    </script>`;
  const { literals, dynamicPrefixes } = scanSource(code, 'js');
  assert.deepEqual(literals.map((l) => l.key).sort(), ['common.ok', 'order.submitBtn', 'order.title']);
  assert.deepEqual(dynamicPrefixes.map((d) => d.prefix).sort(), ['order.status.', 'order.type.']);
});

test('scanSource 识别 .NET L["…"] 与插值前缀', () => {
  const code = `
    <button>@L["order.submitBtn", count]</button>
    var s = _localizer["common.ok"];
    var d = L[$"order.status.{status}"];`;
  const { literals, dynamicPrefixes } = scanSource(code, 'dotnet');
  assert.deepEqual(literals.map((l) => l.key).sort(), ['common.ok', 'order.submitBtn']);
  assert.deepEqual(dynamicPrefixes.map((d) => d.prefix), ['order.status.']);
});

const bundles = {
  'zh-CN': { order: { submitBtn: '去结算 ({count})', title: '订单', status: { paid: '已支付' } }, common: { ok: '确定' } },
  'en-US': { order: { submitBtn: 'Checkout', title: 'Orders' }, common: { ok: 'OK' } }
};

test('analyze：缺失译文、占位符不一致、完成度门槛', () => {
  const r = analyze({ bundles, defaultCulture: 'zh-CN', options: { minCoverage: 90 } });
  assert.equal(r.coverage['en-US'].translated, 3);
  assert.equal(r.coverage['en-US'].total, 4);
  assert.ok(r.errors.some((e) => e.code === 'PLACEHOLDER_MISMATCH' && e.key === 'order.submitBtn'));
  assert.ok(r.errors.some((e) => e.code === 'COVERAGE_BELOW_MIN' && e.culture === 'en-US'));
  assert.ok(r.warnings.some((w) => w.code === 'MISSING_TRANSLATION' && w.message.includes('order.status.paid')));
});

test('analyze：代码里用到却不存在 / 存在却没用到，动态前缀与白名单不误报', () => {
  const usage = {
    literals: new Map([
      ['order.submitBtn', 'a.vue:1'],
      ['order.ghost', 'a.vue:2']
    ]),
    dynamicPrefixes: ['order.status.']
  };
  const r = analyze({ bundles, defaultCulture: 'zh-CN', usage, options: { dynamicKeyPrefixes: [] } });
  assert.ok(r.errors.some((e) => e.code === 'USED_BUT_MISSING' && e.key === 'order.ghost'));
  const unused = r.warnings.find((w) => w.code === 'UNUSED_KEYS');
  assert.ok(unused.message.includes('order.title'));
  assert.ok(unused.message.includes('common.ok'));
  assert.ok(!unused.message.includes('order.status.paid')); // 动态前缀覆盖

  const r2 = analyze({ bundles, defaultCulture: 'zh-CN', usage, options: { dynamicKeyPrefixes: ['common.', 'order.title'] } });
  assert.ok(!r2.warnings.some((w) => w.code === 'UNUSED_KEYS')); // 白名单前缀
});

test('parseArgs 支持 --a b、--a=b、布尔开关与位置参数', () => {
  const a = parseArgs(['push', '--dir', 'x', '--default-ns=common', '--publish']);
  assert.deepEqual(a._, ['push']);
  assert.equal(a.dir, 'x');
  assert.equal(a['default-ns'], 'common');
  assert.equal(a.publish, 'true');
});
