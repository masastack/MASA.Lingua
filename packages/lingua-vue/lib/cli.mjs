import fs from 'node:fs/promises';
import path from 'node:path';
import { analyze, scanSource, sourceKind } from './analyze.mjs';

const SKIP_DIRS = new Set(['node_modules', 'dist', 'build', 'bin', 'obj', '.git', '.next', '.nuxt', '.output', 'coverage']);

export function parseArgs(argv) {
  const args = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const eq = a.indexOf('=');
      if (eq > 0) {
        args[a.slice(2, eq)] = a.slice(eq + 1);
      } else {
        const next = argv[i + 1];
        args[a.slice(2)] = next !== undefined && !next.startsWith('--') ? argv[++i] : 'true';
      }
    } else {
      args._.push(a);
    }
  }
  return args;
}

async function exists(p) {
  try {
    await fs.access(p);
    return true;
  } catch {
    return false;
  }
}

/** 配置优先级：命令行参数 > 环境变量 > lingua.config.json > 默认值。 */
export async function resolveConfig(args, env = process.env, cwd = process.cwd()) {
  const configPath = path.resolve(cwd, args.config || 'lingua.config.json');
  let file = {};
  if (await exists(configPath)) {
    try {
      file = JSON.parse(await fs.readFile(configPath, 'utf8'));
    } catch (e) {
      throw new Error(`无法解析 ${configPath}：${e.message}`);
    }
  }

  const cultures = args.cultures ?? file.cultures ?? env.LINGUA_CULTURES;
  return {
    endpoint: String(args.endpoint ?? env.LINGUA_ENDPOINT ?? file.endpoint ?? 'http://localhost:5000').replace(/\/$/, ''),
    project: args.project ?? env.LINGUA_PROJECT ?? file.project,
    // 写密钥（服务端配置了 Lingua:Distribution:WriteApiKey 时，push 必须携带）。
    // 刻意不读 lingua.config.json：那个文件通常会提交进仓库，密钥只走命令行参数或环境变量（CI 里放进 secret）。
    writeKey: args.key ?? env.LINGUA_WRITE_KEY ?? null,
    cultures: Array.isArray(cultures) ? cultures : cultures ? String(cultures).split(',').map((s) => s.trim()).filter(Boolean) : null,
    defaultCulture: args['default-culture'] ?? file.defaultCulture ?? null,
    out: path.resolve(cwd, args.out ?? file.out ?? 'src/locales'),
    src: path.resolve(cwd, args.src ?? file.src ?? 'src'),
    dir: args.dir ? path.resolve(cwd, args.dir) : null,
    dynamicKeyPrefixes: file.dynamicKeyPrefixes ?? [],
    cwd
  };
}

function requireProject(cfg) {
  if (!cfg.project) {
    throw new Error('缺少项目编码：用 --project、LINGUA_PROJECT 或 lingua.config.json 的 project 指定。');
  }
}

async function request(url, { method = 'GET', headers = {}, body } = {}) {
  const h = { 'X-Lingua-Client': 'cli/lingua-sync', ...headers };
  let res;
  try {
    res = await fetch(url, { method, headers: h, body });
  } catch (e) {
    throw new Error(`无法连接 ${url}：${e.cause?.message ?? e.message}`);
  }
  return res;
}

async function failure(res, what) {
  let detail = '';
  try {
    const text = await res.text();
    try {
      detail = JSON.parse(text).error ?? text;
    } catch {
      detail = text;
    }
  } catch {
    // 读不到响应体就只报状态码
  }
  const hint =
    res.status === 404
      ? '（检查项目编码与是否已发布）'
      : res.status === 401
        ? '（服务端要求写密钥：用 --key 或环境变量 LINGUA_WRITE_KEY 提供，对应服务端的 Lingua:Distribution:WriteApiKey）'
        : res.status === 403
          ? '（写密钥不匹配：检查 --key / LINGUA_WRITE_KEY 与服务端 Lingua:Distribution:WriteApiKey 是否一致）'
          : '';
  return new Error(`${what}失败：HTTP ${res.status} ${detail}${hint}`);
}

async function readLock(out) {
  try {
    return JSON.parse(await fs.readFile(path.join(out, 'lingua.lock.json'), 'utf8'));
  } catch {
    return null;
  }
}

async function fetchManifest(cfg, etag) {
  const url = `${cfg.endpoint}/api/v1/projects/${encodeURIComponent(cfg.project)}/manifest`;
  const res = await request(url, { headers: etag ? { 'If-None-Match': etag } : {} });
  if (res.status === 304) return { notModified: true };
  if (!res.ok) throw await failure(res, '获取 manifest ');
  return { manifest: await res.json(), etag: res.headers.get('etag') };
}

export async function pull(cfg, log = console.log) {
  requireProject(cfg);
  const lock = await readLock(cfg.out);
  const first = await fetchManifest(cfg, lock?.manifestEtag);

  if (first.notModified) {
    const allThere = lock && (await Promise.all(Object.values(lock.cultures).map((c) => exists(path.join(cfg.out, c.file))))).every(Boolean);
    if (allThere) {
      log(`[lingua-sync] 已是最新版本 ${lock.version}（manifest 304）。`);
      return { changed: false, version: lock.version };
    }
  }

  const { manifest, etag } = first.notModified ? await fetchManifest(cfg, null) : first;
  await fs.mkdir(cfg.out, { recursive: true });

  const wanted = cfg.cultures ? new Set(cfg.cultures.map((c) => c.toLowerCase())) : null;
  const cultures = {};
  let changed = false;
  for (const c of manifest.cultures) {
    if (wanted && !wanted.has(c.culture.toLowerCase())) continue;

    const file = `${c.culture}.json`;
    const target = path.join(cfg.out, file);
    const held = lock?.cultures?.[c.culture];
    const headers = held?.etag && (await exists(target)) ? { 'If-None-Match': held.etag } : {};
    const res = await request(`${cfg.endpoint}${c.url}`, { headers });

    if (res.status === 304) {
      cultures[c.culture] = held;
      log(`[lingua-sync] ${file} 未变化`);
      continue;
    }

    if (!res.ok) throw await failure(res, `拉取 ${file} `);
    const text = await res.text();
    await fs.writeFile(target, text, 'utf8');
    cultures[c.culture] = { etag: res.headers.get('etag') ?? c.etag, entryCount: c.entryCount, file };
    changed = true;
    log(`[lingua-sync] 已写入 ${path.relative(cfg.cwd, target)}（${c.entryCount} 条）`);
  }

  await fs.writeFile(
    path.join(cfg.out, 'lingua.lock.json'),
    JSON.stringify(
      {
        project: manifest.project,
        endpoint: cfg.endpoint,
        version: manifest.version,
        defaultCulture: manifest.defaultCulture,
        publishedAt: manifest.publishedAt,
        pulledAt: new Date().toISOString(),
        manifestEtag: etag,
        cultures
      },
      null,
      2
    ) + '\n',
    'utf8'
  );
  log(`[lingua-sync] 完成：版本 ${manifest.version}，lock 已写入 ${path.relative(cfg.cwd, path.join(cfg.out, 'lingua.lock.json'))}`);
  return { changed, version: manifest.version };
}

export async function push(cfg, args, log = console.log) {
  requireProject(cfg);
  const dir = cfg.dir ?? cfg.out;
  if (!(await exists(dir))) throw new Error(`目录不存在：${dir}（用 --dir 指定存量 locales 目录）`);

  const files = (await fs.readdir(dir)).filter((f) => f.endsWith('.json') && f !== 'lingua.lock.json').sort();
  if (files.length === 0) throw new Error(`${dir} 下没有 {culture}.json 文件。`);

  const results = [];
  for (let i = 0; i < files.length; i++) {
    const file = files[i];
    const culture = file.replace(/\.json$/, '');
    const body = await fs.readFile(path.join(dir, file));
    const qs = new URLSearchParams({ culture, defaultNs: args['default-ns'] ?? 'common' });
    if (args.publish === 'true' && i === files.length - 1) qs.set('publish', 'true');

    const url = `${cfg.endpoint}/api/v1/projects/${encodeURIComponent(cfg.project)}/import-json?${qs}`;
    const res = await request(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...(cfg.writeKey ? { 'X-Lingua-Key': cfg.writeKey } : {}) },
      body
    });
    if (!res.ok) throw await failure(res, `导入 ${file} `);

    const r = await res.json();
    results.push(r);
    log(
      `[lingua-sync] ${file}：解析 ${r.totalParsed} 条，新增词条 ${r.addedEntries}，更新译文 ${r.updatedTranslations}` +
        (r.needsReview ? `，${r.needsReview} 条进入待复核` : '') +
        (r.staleMarked ? `，${r.staleMarked} 条因源文案变化待复核` : '')
    );

    if (r.pathConflictCount) {
      const sample = (r.pathConflicts ?? []).slice(0, 3).join('；');
      log(
        `[lingua-sync] 警告：本次导入引入 ${r.pathConflictCount} 处键路径冲突（根 Key 与同名命名空间撞车），发布门禁默认会拦截` +
          (sample ? `：${sample}` : '')
      );
    }

    if (r.caseDuplicateCount) {
      const sample = (r.caseDuplicates ?? []).slice(0, 3).join('；');
      log(
        `[lingua-sync] 提示：${file} 里有 ${r.caseDuplicateCount} 处仅大小写不同的重复词条（命名空间 / Key 不区分大小写，已合并为同一个词条）` +
          (sample ? `：${sample}` : '')
      );
    }

    if (r.published) log('[lingua-sync] 已发布。');
    if (r.publishBlockedReason) log(`[lingua-sync] 导入成功，但发布被门禁拦截：${r.publishBlockedReason}`);
  }

  return results;
}

async function* walk(dir, skip) {
  for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name) || skip.has(path.resolve(full))) continue;
      yield* walk(full, skip);
    } else {
      yield full;
    }
  }
}

export async function scanUsage(srcDir, skipDirs = []) {
  const literals = new Map();
  const dynamicPrefixes = [];
  let files = 0;
  if (!(await exists(srcDir))) return { literals, dynamicPrefixes, files, missingDir: true };

  const skip = new Set(skipDirs.map((d) => path.resolve(d)));
  for await (const file of walk(srcDir, skip)) {
    const kind = sourceKind(path.extname(file));
    if (!kind) continue;
    files++;
    const text = await fs.readFile(file, 'utf8');
    const found = scanSource(text, kind);
    for (const { key, line } of found.literals) {
      if (!literals.has(key)) literals.set(key, `${path.relative(srcDir, file)}:${line}`);
    }
    for (const { prefix } of found.dynamicPrefixes) dynamicPrefixes.push(prefix);
  }

  return { literals, dynamicPrefixes, files };
}

async function loadBundles(cfg, args) {
  const bundles = {};
  if (args.remote === 'true') {
    requireProject(cfg);
    const { manifest } = await fetchManifest(cfg, null);
    for (const c of manifest.cultures) {
      if (cfg.cultures && !cfg.cultures.some((x) => x.toLowerCase() === c.culture.toLowerCase())) continue;
      const res = await request(`${cfg.endpoint}${c.url}`);
      if (!res.ok) throw await failure(res, `拉取 ${c.culture}.json `);
      bundles[c.culture] = await res.json();
    }
    return { bundles, defaultCulture: cfg.defaultCulture ?? manifest.defaultCulture };
  }

  if (!(await exists(cfg.out))) throw new Error(`目录不存在：${cfg.out}。先运行 lingua-sync pull，或用 --remote 直接检查线上版本。`);
  for (const f of (await fs.readdir(cfg.out)).filter((n) => n.endsWith('.json') && n !== 'lingua.lock.json')) {
    const culture = f.replace(/\.json$/, '');
    if (cfg.cultures && !cfg.cultures.some((x) => x.toLowerCase() === culture.toLowerCase())) continue;
    bundles[culture] = JSON.parse(await fs.readFile(path.join(cfg.out, f), 'utf8'));
  }
  const lock = await readLock(cfg.out);
  return { bundles, defaultCulture: cfg.defaultCulture ?? lock?.defaultCulture ?? Object.keys(bundles).find((c) => /^zh/i.test(c)) ?? Object.keys(bundles)[0] };
}

/**
 * 拉取项目的生效术语（GET /api/v1/projects/{code}/glossary，全局术语 + 项目术语）。
 * 默认尽力而为：没配项目或连不上服务时跳过并提示；显式 --glossary 时失败直接报错；--no-glossary 跳过。
 */
export async function fetchGlossary(cfg, args, log = console.log) {
  if (args['no-glossary'] === 'true' || args.glossary === 'false') return null;
  const required = args.glossary === 'true';
  if (!cfg.project) {
    if (required) requireProject(cfg);
    return null;
  }

  try {
    const res = await request(`${cfg.endpoint}/api/v1/projects/${encodeURIComponent(cfg.project)}/glossary`);
    if (!res.ok) throw await failure(res, '获取术语库');
    return (await res.json()).terms ?? [];
  } catch (e) {
    if (required) throw e;
    log(`[lingua-sync] 跳过术语检查：${e.message}（用 --glossary 强制检查，--no-glossary 关闭）`);
    return null;
  }
}

export async function check(cfg, args, log = console.log) {
  const { bundles, defaultCulture } = await loadBundles(cfg, args);
  if (Object.keys(bundles).length === 0) throw new Error('没有找到任何语种文件。');

  const glossary = await fetchGlossary(cfg, args, log);

  const scan = args['no-scan'] === 'true' ? null : await scanUsage(cfg.src, [cfg.out]);
  if (scan?.missingDir) log(`[lingua-sync] 源码目录 ${cfg.src} 不存在，跳过 key 扫描（用 --src 指定）。`);

  const report = analyze({
    bundles,
    defaultCulture,
    usage: scan && !scan.missingDir ? scan : undefined,
    options: {
      dynamicKeyPrefixes: cfg.dynamicKeyPrefixes,
      glossary: glossary ?? undefined,
      minCoverage: args['min-coverage'] !== undefined ? Number(args['min-coverage']) : undefined
    }
  });

  log(`[lingua-sync] 默认语种 ${defaultCulture}，共 ${Object.keys(bundles).length} 个语种` + (scan && !scan.missingDir ? `，扫描 ${scan.files} 个源文件，用到 ${scan.literals.size} 个字面量 key` : ''));
  for (const [culture, c] of Object.entries(report.coverage)) {
    const g = report.glossaryCompliance?.[culture];
    const gText = g && g.percent !== null ? `  术语遵循 ${String(g.percent).padStart(3)}% (${g.followed}/${g.checked})` : '';
    log(`  ${culture.padEnd(8)} 完成度 ${String(c.percent).padStart(5)}%  (${c.translated}/${c.total})${gText}`);
  }
  for (const w of report.warnings) log(`  警告 ${w.message}`);
  for (const e of report.errors) log(`  错误 ${e.message}`);

  const failed = report.errors.length > 0 || (args.strict === 'true' && report.warnings.length > 0);
  log(failed ? `[lingua-sync] 检查未通过：${report.errors.length} 个错误，${report.warnings.length} 个警告。` : `[lingua-sync] 检查通过（${report.warnings.length} 个警告）。`);
  return { ...report, failed };
}

export const HELP = `lingua-sync <command> [options]

命令
  pull    拉取线上已发布版本到本地目录，并写 lingua.lock.json（构建期兜底包）
  push    把存量 locales 目录里的 {culture}.json 导入平台（只写草稿）
  check   完成度 / 占位符检查，并扫描源码里的 key（Vue: $t( t(；.NET: L["…"]）

通用选项（也可写在 lingua.config.json 里）
  --endpoint <url>     Lingua 服务地址        (LINGUA_ENDPOINT)
  --project <code>     项目编码               (LINGUA_PROJECT)
  --cultures a,b       只处理这些语种
  --out <dir>          本地语言包目录，默认 src/locales
  --config <file>      配置文件，默认 ./lingua.config.json

push
  --dir <dir>          存量 locales 目录（默认同 --out）
  --publish            导入后立即发布（受发布门禁约束）
  --key <secret>       写密钥 (LINGUA_WRITE_KEY)：服务端配置了 Lingua:Distribution:WriteApiKey 时必填；
                       密钥不会从 lingua.config.json 读取，CI 里请用环境变量，避免出现在进程列表里

check
  --remote             检查线上版本，而不是本地文件
  --src <dir>          源码目录，默认 src；--no-scan 跳过扫描
  --min-coverage <n>   任一语种完成度低于 n% 则失败
  术语检查：配置了项目时默认拉取 /api/v1/projects/{code}/glossary 做检查（禁止翻译 / 标准译法 / 可接受变体 / 禁用译法），
            违规与发布门禁同口径，视为错误；连不上服务时跳过。
  --glossary           强制检查术语（拉取失败则报错）；--no-glossary 关闭
  --strict             有警告也失败
  配置文件的 dynamicKeyPrefixes 用来声明动态 key 前缀（如 ["order.status."]），避免误报「未使用」

退出码：0 成功；1 失败（检查未通过或请求出错）
`;
