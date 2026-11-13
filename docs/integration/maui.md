# 接入 .NET MAUI

> English summary: integrating Lingua into .NET MAUI (XAML), Blazor Hybrid, WPF and WinForms clients via `MASA.Lingua.SDK.Maui` — a hot-reloading localizer that syncs at startup and on resume. Chinese guide below.

适用：.NET 8 / .NET 10 的 MAUI（XAML 页面），以及 Blazor Hybrid、WPF、WinForms 等任何 .NET 8 / .NET 10 客户端应用。
包名 `MASA.Lingua.SDK.Maui`：**不依赖 MAUI 工作负载**，是普通的 `net8.0;net10.0` 双目标类库，MAUI 的 `net8.0-android` / `net10.0-android` / `ios` / `maccatalyst` / `windows` 目标都可以引用。

和服务端 SDK（ASP.NET Core / Blazor Server）的区别：客户端应用跑在用户设备上，所以

- 语言包缓存在**本机私有目录**，启动先读缓存（离线可用），再联网同步；
- 没有后台常驻同步服务和 SSE（移动端后台连接会被系统掐断），改为**启动时 + 回到前台时**同步一次；
- 提供**可绑定的索引器**，XAML 里直接绑定，语种切换或同步到新内容时界面自动刷新。

## 前置条件

- 项目已在 Lingua 发布；知道项目编码（由平台统一分配，见 [README](README.md)）和服务地址。
- 服务地址要用 **HTTPS**（Android 默认禁止明文 HTTP，iOS 的 ATS 也一样）。Android 模拟器访问本机开发服务要用 `http://10.0.2.2:端口`，且需要在开发配置里允许明文。
- 不需要密钥（读发布产物匿名可读；项目写密钥只用于服务端 SDK 的缺词上报，客户端 SDK 不涉及）。

## 1. 引用

从 nuget.org 安装（版本以 nuget.org 上的实际发布号为准，当前 `0.0.1-preview.1`）：

```xml
<ItemGroup>
  <PackageReference Include="MASA.Lingua.SDK.Maui" Version="0.0.1-preview.1" />
</ItemGroup>
```

用中央包管理时版本写在 `Directory.Packages.props`，csproj 里只留包名：

```xml
<PackageVersion Include="MASA.Lingua.SDK.Maui" Version="0.0.1-preview.1" />
```

## 2. 注册

```csharp
// MauiProgram.cs
using MASA.Lingua.SDK.Maui;

builder.Services.AddLinguaMaui(o =>
{
    o.ProjectCode = "my-maui-app";              // 你的项目编码
    o.ServerEndpoint = "https://lingua.example.com";
});
```

只需要这两项。语种和默认语种来自平台，客户端**没有**这些配置，也没有 API Key。其他选项：

| 选项 | 默认 | 说明 |
|---|---|---|
| `LocalJsonDirectory` | `"i18n"` | 相对路径时放在应用私有数据目录：`{LocalApplicationData}/Lingua/{项目}/i18n`（Android / iOS 是沙盒私有目录，卸载应用一并删除）；绝对路径原样使用。可从 `localizer.CacheDirectory` 读到最终位置 |
| `ProjectCode` | 必填 | 空值在 `AddLinguaMaui` 注册期抛 `InvalidOperationException`（不经 DI 直接 `new LinguaLocalizer` 时抛 `ArgumentException`） |
| `ServerEndpoint` | 无 | 留空是合法的「纯离线 / 仅 Seed」模式；填了就必须是 `http(s)` 绝对地址，漏写 scheme（`lingua.example.com`）或写成 `localhost:5000` 会在注册期抛 `InvalidOperationException`，开发期第一次启动就能发现 |

`AddLinguaMaui` 注册的命名 `HttpClient`（`"LinguaClient"`）单次请求超时为 30 秒（`LinguaMauiExtensions.DefaultRequestTimeout`），
默认的 100 秒在「连得上但不回包」的网络下会长时间占着同步闸门。需要调整时对同名客户端再配置一次即可覆盖：
`services.AddHttpClient("LinguaClient").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));`

没有轮询选项：更新只在应用每次启动时拉取（`InitializeAsync` 内置），不读 `PollInterval`（那是服务端 SDK 的配置）。
`EnableServerEvents` / `EnableMissingKeyTelemetry` / `WriteKey` 在客户端应用里都不生效（见下）。
**MAUI 端不做缺词上报**：密钥无法安全保管在终端设备（包可被反编译），且服务端只接受携带项目写密钥的上报，
因此客户端 SDK 已移除该能力；漏翻由服务端应用的缺词上报与平台的完成度统计、发布门禁发现。`EnableMissingKeyTelemetry` 对 MAUI 无效。

## 3. 启动与回到前台

```csharp
// App.xaml.cs
public partial class App : Application
{
    private readonly LinguaLocalizer _lingua;

    public App(LinguaLocalizer lingua)
    {
        _lingua = lingua;
        InitializeComponent();

        Resources["Loc"] = lingua;          // 先放进资源，页面里用 {StaticResource Loc} 引用
        _ = lingua.InitializeAsync();       // 立即读本机缓存；联网同步在后台进行，完成后界面自动刷新

        MainPage = new AppShell();
    }

    protected override void OnResume() => _ = _lingua.TrySyncAsync();   // 回到前台：没变化是一次 304
}
```

- `InitializeAsync()` 先**同步**读完本机缓存才第一次 `await`，所以紧接着创建的页面已经有文案；
  联网部分在后台跑，同步到新内容后通过 `PropertyChanged` 刷新界面。应用每次重启都会经过这一步，拿到当时线上的最新版本。
- `InitializeAsync` / `TrySyncAsync` 只要联网同步失败就**不抛异常**、继续用缓存，除非是你自己传入的 `CancellationToken` 被取消。
  失败包括：网络不可用 / 超时 / 服务端报错、响应不是合法 JSON（例如酒店、机场 Wi-Fi 的登录页返回了 HTML）、磁盘写不进、地址配错等。
  失败原因记录在 `lingua.LastSyncError`（之后一次同步成功就清空），排查「为什么一直没更新」先看它；需要在调用处直接拿到异常时用 `SyncAsync()`（它照常抛出，同时也记入 `LastSyncError`）。
  同时每次失败还会往 `ILogger<LinguaLocalizer>` 记一条 Warning（带异常），用 `AddLinguaMaui` 注册时自动取容器里的日志器；手工 `new LinguaLocalizer(...)` 想要日志就传 `logger:` 参数，不传则不记日志。
  调用方主动取消不算故障，不记日志也不写 `LastSyncError`。并发的同步在内部排队，`LastSyncError` 始终对应「最后完成的那一次同步」。
- **不要在应用启动路径上 `await` `InitializeAsync()`**：它先同步读完本机缓存，然后才联网；弱网 / 黑洞网络下联网部分要等到 HTTP 超时才返回，
  `await` 它会把这段时间都算进启动耗时。上面的 `_ = lingua.InitializeAsync();` 才是推荐写法。
- 通知所在的线程：同步里刻意没有使用 `ConfigureAwait(false)`，`PropertyChanged` 与 `ILinguaChangeNotifier.OnCultureUpdated` 在**调用同步方法的上下文**里触发。
  按上面的写法在 UI 线程发起（`App` 构造、`OnResume`），通知就在 UI 线程上；从后台线程调用（例如 `Task.Run`、网络状态变化回调）则通知也在后台线程，
  需要自己切回 UI 线程再更新界面。
- 没有常驻轮询；上面的 `OnResume` 是可选的补充时机（没变化只是一次 304），不需要更及时的更新可以不写。
- 示例里的 `MainPage = new AppShell()` 与 `OnResume` 是 .NET 8 模板的写法。较新的 MAUI 版本可能推荐用 `CreateWindow` 与 `Window` 的生命周期事件代替，
  请以你所用 MAUI 版本的项目模板为准；本 SDK 只需要你在「首屏前」与「回到前台」这两个时机各调用一次上面的方法，与具体写法无关。

## 4. XAML 里使用

```xml
<Label Text="{Binding [order.submitBtn], Source={StaticResource Loc}}" />
<Button Text="{Binding [common.ok], Source={StaticResource Loc}}" />
```

`[key]` 是索引器绑定。key 里的 `:` 等价于 `.`。取不到 key 时显示 key 本身。

带占位符的文案，在代码里取：

```csharp
label.Text = lingua.Get("order.submitBtn", new { count = 3 });                     // 去结算 (3)
label.Text = lingua.Get("common:welcomeUser", new { userName = "Alice" });
```

切换语种：

```csharp
lingua.SetCulture("en-US");     // 所有索引器绑定自动刷新
```

`Culture` 默认取设备的界面语种。平台没有发布该语种时，取词自动回退到平台默认语种（并先回退到中性语种，如 `zh-CN`→`zh`）。

两点需要应用自己处理：

- **`SetCulture` 只改内存，不持久化。** 用户在语言选择器里选的语言，重启后会回到设备语种。需要记住用户选择时，
  应用自己用 `Preferences` 之类保存，并在启动时（`InitializeAsync` 之前）恢复：
  `if (Preferences.Get("lang", null) is { } saved) lingua.SetCulture(saved);`
- **iOS 上如果取到的界面语种总是英文**（而设备实际是中文等），请先检查应用的 `Info.plist` 是否声明了 `CFBundleLocalizations`
  （列出应用支持的语言）。这是 iOS 上常见的现象：应用没有声明本地化时，系统只会向它报告开发语言。
  本 SDK 的语种来自平台、客户端没有语种配置，而这份声明是随应用包发布的静态清单，所以平台新增的语种若不在清单里，
  iOS 设备可能不会自动选中它，需要用户在语言选择器里手动选。**这一条是经验性提示，作者没有在真机上验证过**，请以你的设备表现为准。

语言选择器直接读 `localizer.AvailableCultures`——它就是平台 manifest 里的语种清单（启动时先用本机缓存兜底，对账成功后以平台为准），不用再自己请求 manifest：

```csharp
picker.ItemsSource = lingua.AvailableCultures;
```

## 5. 首次安装且离线：内置语言包（可选）

发布产物就是普通静态 JSON（`GET {服务地址}/i18n/{项目}/{culture}.json`，见 [README](README.md)），
用任意 HTTP 工具把已发布语种下载进工程即可，不需要任何额外工具链。
**一定要带 `?format=flat`**：MAUI 客户端按扁平格式取词（与联网同步下载的一致），嵌套格式无法表达「项目根 key 与同名命名空间」
这类键路径冲突，内置包里这类词条会取不到值：

```powershell
Invoke-WebRequest "https://lingua.example.com/i18n/my-maui-app/zh-CN.json?format=flat" -OutFile Resources/Raw/zh-CN.json
Invoke-WebRequest "https://lingua.example.com/i18n/my-maui-app/en-US.json?format=flat" -OutFile Resources/Raw/en-US.json
Invoke-WebRequest "https://lingua.example.com/i18n/my-maui-app/ja-JP.json?format=flat" -OutFile Resources/Raw/ja-JP.json
```

`Resources/Raw` 下的文件默认就是 `MauiAsset`。启动时在 `InitializeAsync` 之前放入。
读包内文件是异步的，所以把「Seed + 初始化」放进一个异步方法，在构造函数里**不 `await`** 地启动它（方法内部 `await` 没问题，关键是启动路径别等它）：

```csharp
// App.xaml.cs
public App(LinguaLocalizer lingua)
{
    _lingua = lingua;
    InitializeComponent();
    Resources["Loc"] = lingua;

    _ = InitLinguaAsync();              // 不要 await：联网部分在弱网下可能很慢
    MainPage = new AppShell();
}

private async Task InitLinguaAsync()
{
    foreach (var culture in new[] { "zh-CN", "en-US", "ja-JP" })
    {
        using var stream = await FileSystem.OpenAppPackageFileAsync($"{culture}.json");
        _lingua.Seed(culture, stream);
    }

    await _lingua.InitializeAsync();    // Seed 必须先于它；本机缓存同步读完，随后才联网
}
```

首次安装且离线时，页面在 Seed 完成前可能短暂显示 key；`InitializeAsync` 读完缓存后会触发界面刷新。

`Seed` 只在**缓存里还没有该语种**时生效，已有缓存（通常比包内的新）不会被覆盖。
语种名会先规范化再落盘（`zh-cn`、`zh_CN` 都按 `zh-CN` 处理），因为 Android 的文件系统区分文件名大小写、iOS 不区分，不规范化会让两个平台的缓存内容分叉。
内置文件内容不是合法 JSON 时 `Seed` 会抛异常（这是打包错误，应当在开发期暴露），并且不会留下坏缓存文件。

## 6. 标准 `IStringLocalizer`（可选）

`AddLinguaMaui` 同时注册了 `IStringLocalizer` 和 `IStringLocalizer<T>`，共享库或 Blazor Hybrid 页面里可以照常 `@inject IStringLocalizer L`、`L["order.submitBtn", new { count = 3 }]`。
它们读的是同一个 `LinguaLocalizer`，当前语种以 `lingua.Culture` 为准（不是线程的 `CurrentUICulture`）。
Blazor Hybrid 里要让文案在同步后自动重绘，订阅 `ILinguaChangeNotifier.OnCultureUpdated` 后调用页面刷新即可（MAUI 模板已内置）。

## 同步是怎么工作的

1. 请求 manifest（带 `If-None-Match`，没变 304）；平台未发布（404）静默返回。本机有缓存文件丢失（被系统清理 / 用户删除）时不带 `If-None-Match`，拉全量 manifest 把缺的语种补回来。
2. 版本变了，只下载 ETag **变化或缓存文件缺失**的语种：**先校验内容，再原子写入缓存目录，最后才更新内存**；已下载的 ETag 保存在缓存目录的 `lingua.etags`（刻意不用 `.json` 后缀，免得被当成语种文件），下次启动不重复下载。
   这个顺序两个方向都不会留下「半个」状态：内容是「200 但不是合法 JSON」（登录页、代理改写）时，校验阶段就失败，磁盘与内存都原样保留；内容合法但写不进磁盘（磁盘满、权限、被占用）时，内存与 ETag 也不会推进，不会出现「本次会话是 new 文案、重启后又回到旧缓存」的假象，下次同步重试。
3. 全部语种成功才推进版本与语种清单；中途失败下次同步重试，语言切换器不会列出还没落盘的语种。平台已移除的语种会顺手清掉过期 ETag（缓存文件保留作离线兜底）。
4. 同一时刻只有一个同步在跑（`SemaphoreSlim`），并发调用排队。
5. 语种名会变成文件名，只接受字母 / 数字 / `-` / `_` 且长度 ≤ 35，含路径分隔符等非法字符的一律拒绝。
6. 写盘用 `.tmp` + `File.Move(overwrite)` 原子替换，并对 Windows / 移动平台的文件占用做重试（最多 5 次）。
7. 同步失败不影响已有缓存与内存词条；`TrySyncAsync` / `InitializeAsync` 不向外抛，失败原因记在 `LastSyncError`（见第 3 节）。

## 限制

- 没有 SSE 与轮询，**发布后设备在下一次启动、回到前台时才能拿到**。
- 不做缺词上报（见第 2 节）：密钥无法安全保管在终端设备，服务端也只接受携带密钥的上报。
- 绑定的是字符串索引器，**不是**资源文件；`x:Static` 引用 `.resx` 的写法不受影响，也不会被 Lingua 接管。

## 验证过什么

`MASA.Lingua.SDK.Maui` 是普通 `net8.0;net10.0` 双目标类库，在没有 MAUI 工作负载的环境里编译，`dotnet pack` 能打出包。
自动化测试（`tests/MASA.Lingua.Tests/MauiClientTests.cs`，27 条，一部分对着进程内的 Lingua 服务，一部分用桩 HTTP 处理器）覆盖：

- 初始化后三个平台语种都落盘并可取词，占位符、语种切换、缺失 key、未发布语种回退到平台默认语种都正确；
- 第二次同步为「无变化」，模拟重启后不重复下载（ETag 已持久化）；
- 缓存文件被删除后，下一次同步自动补回（不会因 manifest 304 跳过补下载）；
- 语种下载中途失败时同步报错，`AvailableCultures` 不暴露还没落盘的语种，重试成功后才更新；
- 服务端不可达时 `InitializeAsync` / `TrySyncAsync` 不抛异常、缓存继续可用，`SyncAsync` 抛出；
- `Seed` 只在没有缓存时生效，并拒绝路径穿越的语种名；
- 项目未发布时不抛异常；
- 即使误开 `EnableMissingKeyTelemetry`，客户端也不发任何 POST（缺词上报已移除的回归守卫）；
- `AddLinguaMaui` 注册的 `LinguaLocalizer`、`IStringLocalizer`、`IStringLocalizer<T>`、通知器可用，默认缓存目录在本地应用数据目录下；
- `AddLinguaMaui` 注册期校验：`ServerEndpoint` 漏写 scheme / 写成 `localhost:5000` / 非 http(s) 抛 `InvalidOperationException`，留空与 `http(s)` 地址放行，`ProjectCode` 为空抛错；命名 `HttpClient` 的默认超时与应用覆盖；
- `TrySyncAsync` / `InitializeAsync` 在登录页 HTML、地址配错等各种失败下不抛，失败原因进 `LastSyncError`，之后同步成功清空；调用方主动取消仍抛出且不记为失败；
- 同步失败往 `ILogger` 记一条带异常的 Warning（取消与成功不记）；并发同步在闸门内串行，`LastSyncError` 最终对应最后完成的那一次；
- 某语种返回「200 + 声称是 JSON 的 HTML」时，磁盘上的旧缓存文件内容不变、内存里的旧词条不变、没有 `.tmp` 残留，恢复后重新下载；
- 内容合法但写不进磁盘时（缓存目录被一个普通文件占住），同步失败、内存词条与 `Version` / `AvailableCultures` 与 ETag 都不推进，`Seed` 也不把内容放进内存；障碍移除后两条路径都能恢复；
- `Seed` 把语种名规范化后落盘（`zh-cn` → `zh-CN.json`），内容非法时抛出且不留坏缓存；
- 线程契约：在单线程上下文（模拟 UI 线程）里发起同步，所有 `PropertyChanged` 与 `OnCultureUpdated` 都在该线程触发——给同步里的 `await` 补 `ConfigureAwait(false)` 会让这条测试失败。

**没有验证**（这台机器没有 `maui` 工作负载，也没有真机 / 模拟器，没法编译和运行 MAUI 应用）：

- 第 3、4、5 节里 MAUI 特有的代码：`App.xaml.cs`、`OnResume`、XAML 中 `{Binding [order.submitBtn], Source=...}` 的索引器绑定写法（key 里带 `.`）、`FileSystem.OpenAppPackageFileAsync`、`Resources/Raw` 打包；
- 真机 / 模拟器上的缓存目录位置和 HTTPS 访问；
- 下列平台行为只是经验判断，没有在任何设备上确认：iOS 上界面语种受 `Info.plist` 本地化声明影响（第 4 节）；MAUI 在 Android / iOS 上默认使用哪种 `HttpClient` 处理器（原生还是 `SocketsHttpHandler`），以及它对 ETag / 304 与响应压缩的处理；较新 MAUI 版本里 `Application.MainPage` / `OnResume` 的推荐写法；
- `net8.0` 目标只做了编译与依赖检查，自动化测试只在 `net10.0` 上执行。

首次在 MAUI 工程里使用时，如果索引器绑定在 key 含 `.` 时取不到值，可以把绑定改成绑定到一个封装属性，或告诉我，我再调整。
