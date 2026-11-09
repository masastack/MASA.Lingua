#!/usr/bin/env node
/**
 * lingua-sync：Lingua 平台的构建期 / CI 命令行工具（零依赖，Node 18+）。
 *
 *   lingua-sync pull                      拉取线上已发布版本，写 {culture}.json 与 lingua.lock.json
 *   lingua-sync push --dir src/locales    把存量 locales 导入平台（草稿）
 *   lingua-sync check --remote            完成度 / 占位符 / 代码 key 扫描
 *
 * 详细选项见 lingua-sync --help。
 */
import { HELP, check, parseArgs, pull, push, resolveConfig } from '../lib/cli.mjs';

async function main() {
  const args = parseArgs(process.argv.slice(2));
  // 兼容旧用法：不带子命令、只有 --endpoint / --project 时等同于 pull
  const command = args._[0] ?? (args.project || args.endpoint ? 'pull' : undefined);

  if (!command || command === 'help' || args.help === 'true') {
    console.log(HELP);
    return;
  }

  const cfg = await resolveConfig(args);
  switch (command) {
    case 'pull':
      await pull(cfg);
      break;
    case 'push':
      await push(cfg, args);
      break;
    case 'check': {
      const result = await check(cfg, args);
      if (result.failed) process.exitCode = 1;
      break;
    }
    default:
      throw new Error(`未知命令 "${command}"。运行 lingua-sync --help 查看用法。`);
  }
}

main().catch((err) => {
  console.error(`[lingua-sync] ${err.message}`);
  process.exit(1);
});
