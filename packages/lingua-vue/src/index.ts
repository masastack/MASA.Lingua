export interface LinguaVueOptions {
  /** Lingua 服务端地址，例如 https://lingua.internal.com */
  endpoint: string;
  /** 项目编码，例如 shop-vue */
  project: string;
  /** 项目内打包内置的本地 locales/{culture}.json，用于首屏 0ms 同步兜底 */
  staticBundles?: Record<string, Record<string, any>>;
  /** 热更新：轮询 manifest 的间隔（毫秒），默认 60000；0 表示不轮询。仅在调用 startHotUpdate 后生效 */
  pollIntervalMs?: number;
  /** 热更新：是否订阅服务端 SSE 发布事件（默认 true）。仅在调用 startHotUpdate 后生效 */
  enableServerEvents?: boolean;
  /** 是否在请求头里带上客户端标识与当前版本（X-Lingua-Client / X-Lingua-Version），用于平台「采用情况」统计，默认 true */
  reportAdoption?: boolean;
  /** 客户端类型标识，默认 "vue" */
  clientName?: string;
  /** 热更新完成后的回调（例如弹一个“文案已更新”的提示） */
  onUpdated?: (info: { version: string; cultures: string[] }) => void;
}

interface ManifestCulture {
  culture: string;
  etag: string;
  url: string;
}

interface ProjectManifest {
  project: string;
  version: string;
  cultures: ManifestCulture[];
}

/**
 * Vue 3 薄适配器：
 * 1. 首屏立即注入打包内置的本地 {culture}.json 与 localStorage 缓存（0ms 无闪烁）；
 * 2. 永远只请求固定文件名的 /i18n/{project}/{culture}.json，完全交由浏览器原生 Disk Cache + ETag/304 协商缓存；
 * 3. startHotUpdate()：SSE + 轮询 manifest 兜底，发布后无需刷新页面即可看到新文案（断线自动退避重连）。
 *
 * 注意：本适配器不做缺词上报——密钥无法安全保管在浏览器端，且服务端只接受携带项目写密钥的上报；
 * 前端漏翻由服务端应用的上报与 lingua-sync check 发现。
 */
export function setupLinguaVue(i18n: any, options: LinguaVueOptions) {
  const base = options.endpoint.replace(/\/$/, '');
  const storagePrefix = `lingua:${options.project}:`;

  // 每个页面实例一个标识，服务端据此去重统计「有多少实例在用哪个版本」
  const instanceId = `${options.clientName ?? 'vue'}/${Math.random().toString(36).slice(2, 14)}`;
  let currentVersion: string | null = null;
  const loadedCultures = new Set<string>();

  function adoptionHeaders(): Record<string, string> {
    if (options.reportAdoption === false) {
      return {};
    }
    const headers: Record<string, string> = { 'X-Lingua-Client': instanceId };
    if (currentVersion) {
      headers['X-Lingua-Version'] = currentVersion;
    }
    return headers;
  }

  /** 所有请求共用的头：采用情况上报 */
  function requestHeaders(): Record<string, string> {
    return adoptionHeaders();
  }

  function readCache(cacheKey: string): { etag?: string; data?: any } | null {
    if (typeof localStorage === 'undefined') {
      return null;
    }
    try {
      const raw = localStorage.getItem(cacheKey);
      return raw ? JSON.parse(raw) : null;
    } catch {
      return null;
    }
  }

  function writeCache(cacheKey: string, etag: string | null, data: any) {
    if (typeof localStorage === 'undefined') {
      return;
    }
    try {
      localStorage.setItem(cacheKey, JSON.stringify({ etag, data }));
    } catch {
      // 存储已满或被禁用：忽略，下次走网络即可
    }
  }

  /**
   * 拉取并应用某个语种。
   * @param replace true 表示整体替换该语种的消息（热更新：被删除的词条也会消失）；false 表示合并（首屏）。
   */
  async function syncLocale(culture: string, replace = false): Promise<boolean> {
    const cacheKey = `${storagePrefix}${culture}`;
    const cached = readCache(cacheKey);
    loadedCultures.add(culture);

    // 1. 首屏同步合并项目内静态 JSON 与 localStorage 缓存，保证 0ms 首屏渲染
    if (!replace) {
      if (options.staticBundles?.[culture]) {
        i18n.global.mergeLocaleMessage(culture, options.staticBundles[culture]);
      }
      if (cached?.data) {
        i18n.global.mergeLocaleMessage(culture, cached.data);
      }
    }

    // 2. 请求固定名字的静态文件 {culture}.json（带 ETag 协商，未更新时返回 304 零流量传输）
    //    服务端已统一 no-cache + 强 ETag，这里首屏与热更新都不再依赖浏览器缓存的有效期
    const url = `${base}/i18n/${options.project}/${culture}.json`;
    const headers: Record<string, string> = { ...requestHeaders() };
    if (cached?.etag) {
      headers['If-None-Match'] = cached.etag;
    }

    // 首屏也要 no-cache：否则浏览器可能直接用 max-age 内的旧文件，发布后首屏文案不更新。
    // 已有 localStorage 里的 ETag 会带 If-None-Match，未变时仍是 304（零流量）。
    const res = await fetch(url, { headers, cache: 'no-cache' });
    if (res.status === 200) {
      const data = await res.json();
      const etag = res.headers.get('ETag');
      if (replace) {
        i18n.global.setLocaleMessage(culture, data);
      } else {
        i18n.global.mergeLocaleMessage(culture, data);
      }
      writeCache(cacheKey, etag, data);
      return true;
    }

    return false;
  }

  async function fetchManifest(): Promise<ProjectManifest | null> {
    const res = await fetch(`${base}/api/v1/projects/${options.project}/manifest`, {
      headers: requestHeaders(),
      cache: 'no-cache'
    });
    return res.ok ? ((await res.json()) as ProjectManifest) : null;
  }

  /** 检查 manifest；版本变了就刷新已加载的语种。返回是否发生了更新。 */
  async function refresh(): Promise<boolean> {
    const manifest = await fetchManifest();
    if (!manifest || manifest.version === currentVersion) {
      return false;
    }

    const first = currentVersion === null;
    currentVersion = manifest.version;
    if (first) {
      // 首次拿到版本号只用于上报，页面已有的内容在 syncLocale 时已经是最新的
      return false;
    }

    const updated: string[] = [];
    for (const culture of loadedCultures) {
      if (await syncLocale(culture, true)) {
        updated.push(culture);
      }
    }
    if (updated.length > 0) {
      options.onUpdated?.({ version: manifest.version, cultures: updated });
    }
    return updated.length > 0;
  }

  /**
   * 开启热更新：订阅 SSE（秒级）+ 定时轮询 manifest（兜底，通常只是一次 304）。
   * 返回停止函数（例如在 HMR / 卸载时调用）。
   */
  function startHotUpdate(): () => void {
    let stopped = false;
    let source: EventSource | null = null;
    let backoff = 1000;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let pollTimer: ReturnType<typeof setInterval> | null = null;
    let running = false;
    let again = false;

    // 串行化：连续事件合并成一次刷新，避免并发请求互相覆盖
    const trigger = async () => {
      if (running) {
        again = true;
        return;
      }
      running = true;
      try {
        do {
          again = false;
          try {
            await refresh();
          } catch {
            // 网络异常：等下一次事件或轮询
          }
        } while (again && !stopped);
      } finally {
        running = false;
      }
    };

    const connect = () => {
      if (stopped || options.enableServerEvents === false || typeof EventSource === 'undefined') {
        return;
      }
      source = new EventSource(`${base}/api/v1/stream?project=${encodeURIComponent(options.project)}`);
      source.onopen = () => {
        backoff = 1000;
        void trigger(); // 重连后先对一次账，补上断线期间错过的发布
      };
      source.onmessage = () => {
        void trigger();
      };
      source.onerror = () => {
        // EventSource 自带重连，但代理 / 服务重启后常卡死；这里自己关掉并按指数退避重建
        source?.close();
        source = null;
        if (stopped) {
          return;
        }
        reconnectTimer = setTimeout(connect, backoff);
        backoff = Math.min(backoff * 2, 30000);
      };
    };

    void trigger(); // 立即取一次版本号
    connect();

    const interval = options.pollIntervalMs ?? 60000;
    if (interval > 0) {
      pollTimer = setInterval(() => void trigger(), interval);
    }

    return () => {
      stopped = true;
      source?.close();
      if (reconnectTimer) clearTimeout(reconnectTimer);
      if (pollTimer) clearInterval(pollTimer);
    };
  }

  return { syncLocale, refresh, startHotUpdate };
}
