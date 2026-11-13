import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { push, resolveConfig } from '../lib/cli.mjs';

/** 起一个最小的 import-json 服务端，记录收到的请求头；status 控制应答（模拟密钥门禁）。 */
async function startServer(respond) {
  const seen = [];
  const server = http.createServer((req, res) => {
    seen.push({ method: req.method, url: req.url, headers: req.headers });
    req.resume();
    req.on('end', () => respond(req, res));
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address();
  return { seen, endpoint: `http://127.0.0.1:${port}`, close: () => new Promise((resolve) => server.close(resolve)) };
}

const ok = (_req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ totalParsed: 1, addedEntries: 1, updatedTranslations: 1 }));
};

async function localesDir() {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'lingua-push-'));
  await fs.writeFile(path.join(dir, 'en-US.json'), JSON.stringify({ hello: 'Hello' }));
  return dir;
}

test('resolveConfig：写密钥只来自 --key 或 LINGUA_WRITE_KEY，不读配置文件', async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'lingua-cfg-'));
  await fs.writeFile(path.join(dir, 'lingua.config.json'), JSON.stringify({ project: 'p', key: 'from-file' }));

  assert.equal((await resolveConfig({}, {}, dir)).writeKey, null);
  assert.equal((await resolveConfig({}, { LINGUA_WRITE_KEY: 'from-env' }, dir)).writeKey, 'from-env');
  assert.equal((await resolveConfig({ key: 'from-arg' }, { LINGUA_WRITE_KEY: 'from-env' }, dir)).writeKey, 'from-arg');
});

test('push：配置了写密钥就带 X-Lingua-Key 请求头', async () => {
  const server = await startServer(ok);
  try {
    const dir = await localesDir();
    const cfg = await resolveConfig({ endpoint: server.endpoint, project: 'shop', dir, key: 'secret-1' }, {}, dir);
    await push(cfg, {}, () => {});

    assert.equal(server.seen.length, 1);
    assert.equal(server.seen[0].method, 'POST');
    assert.equal(server.seen[0].headers['x-lingua-key'], 'secret-1');
  } finally {
    await server.close();
  }
});

test('push：没配置写密钥就不发 X-Lingua-Key（服务端未启用门禁时保持原样）', async () => {
  const server = await startServer(ok);
  try {
    const dir = await localesDir();
    const cfg = await resolveConfig({ endpoint: server.endpoint, project: 'shop', dir }, {}, dir);
    await push(cfg, {}, () => {});

    assert.equal(server.seen.length, 1);
    assert.equal('x-lingua-key' in server.seen[0].headers, false);
  } finally {
    await server.close();
  }
});

test('push：401 / 403 的报错告诉用户怎么提供或修正写密钥', async () => {
  for (const [status, pattern] of [
    [401, /LINGUA_WRITE_KEY/],
    [403, /不匹配/]
  ]) {
    const server = await startServer((_req, res) => {
      res.writeHead(status);
      res.end();
    });
    try {
      const dir = await localesDir();
      const cfg = await resolveConfig({ endpoint: server.endpoint, project: 'shop', dir }, {}, dir);
      await assert.rejects(push(cfg, {}, () => {}), (err) => {
        assert.match(err.message, new RegExp(`HTTP ${status}`));
        assert.match(err.message, pattern);
        return true;
      });
    } finally {
      await server.close();
    }
  }
});
