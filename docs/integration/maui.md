# 接入 .NET MAUI

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

从内部 NuGet 源安装（版本以源上实际发布号为准，当前 `1.3.0`）：

```xml
<ItemGroup>
  <PackageReference Include="MASA.Lingua.SDK.Maui" Version="1.3.0" />
</ItemGroup>
```

用中央包管理时版本写在 `Directory.Packages.props`，csproj 里只留包名：

```xml
<PackageVersion Include="MASA.Lingua.SDK.Maui" Version="1.3.0" />
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
| `ProjectCode` | 必填 | 空值在 `LinguaLocalizer` 构造时直接抛 `ArgumentException` |

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
- 网络不可用、超时时 `InitializeAsync` / `TrySyncAsync` 不抛异常，继续用缓存。需要拿到错误时用 `SyncAsync()`。
- 没有常驻轮询；上面的 `OnResume` 是可选的补充时机（没变化只是一次 304），不需要更及时的更新可以不写。

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

语言选择器直接读 `localizer.AvailableCultures`——它就是平台 manifest 里的语种清单（启动时先用本机缓存兜底，对账成功后以平台为准），不用再自己请求 manifest：

```csharp
picker.ItemsSource = lingua.AvailableCultures;
```

## 5. 首次安装且离线：内置语言包（可选）

发布产物就是普通静态 JSON（`GET {服务地址}/i18n/{项目}/{culture}.json`，见 [README](README.md)），
用任意 HTTP 工具把已发布语种下载进工程即可，不需要任何额外工具链：

```powershell
Invoke-WebRequest https://lingua.example.com/i18n/my-maui-app/zh-CN.json -OutFile Resources/Raw/zh-CN.json
Invoke-WebRequest https://lingua.example.com/i18n/my-maui-app/en-US.json -OutFile Resources/Raw/en-US.json
Invoke-WebRequest https://lingua.example.com/i18n/my-maui-app/ja-JP.json -OutFile Resources/Raw/ja-JP.json
```

`Resources/Raw` 下的文件默认就是 `MauiAsset`。启动时在 `InitializeAsync` 之前放入：

```csharp
foreach (var culture in new[] { "zh-CN", "en-US", "ja-JP" })
{
    using var stream = await FileSystem.OpenAppPackageFileAsync($"{culture}.json");
    lingua.Seed(culture, stream);
}
await lingua.InitializeAsync();
```

`Seed` 只在**缓存里还没有该语种**时生效，已有缓存（通常比包内的新）不会被覆盖。

## 6. 标准 `IStringLocalizer`（可选）

`AddLinguaMaui` 同时注册了 `IStringLocalizer` 和 `IStringLocalizer<T>`，共享库或 Blazor Hybrid 页面里可以照常 `@inject IStringLocalizer L`、`L["order.submitBtn", new { count = 3 }]`。
它们读的是同一个 `LinguaLocalizer`，当前语种以 `lingua.Culture` 为准（不是线程的 `CurrentUICulture`）。
Blazor Hybrid 里要让文案在同步后自动重绘，订阅 `ILinguaChangeNotifier.OnCultureUpdated` 后调用页面刷新即可（MAUI 模板已内置）。

## 同步是怎么工作的

1. 请求 manifest（带 `If-None-Match`，没变 304）；平台未发布（404）静默返回。本机有缓存文件丢失（被系统清理 / 用户删除）时不带 `If-None-Match`，拉全量 manifest 把缺的语种补回来。
2. 版本变了，只下载 ETag **变化或缓存文件缺失**的语种，**原子写入缓存目录**后更新内存；已下载的 ETag 保存在缓存目录的 `lingua.etags`（刻意不用 `.json` 后缀，免得被当成语种文件），下次启动不重复下载。
3. 全部语种成功才推进版本与语种清单；中途失败下次同步重试，语言切换器不会列出还没落盘的语种。平台已移除的语种会顺手清掉过期 ETag（缓存文件保留作离线兜底）。
4. 同一时刻只有一个同步在跑（`SemaphoreSlim`），并发调用排队。
5. 语种名会变成文件名，只接受字母 / 数字 / `-` / `_` 且长度 ≤ 35，含路径分隔符等非法字符的一律拒绝。
6. 写盘用 `.tmp` + `File.Move(overwrite)` 原子替换，并对 Windows / 移动平台的文件占用做重试（最多 5 次）。

## 限制

- 没有 SSE 与轮询，**发布后设备在下一次启动、回到前台时才能拿到**。
- 不做缺词上报（见第 2 节）：密钥无法安全保管在终端设备，服务端也只接受携带密钥的上报。
- 绑定的是字符串索引器，**不是**资源文件；`x:Static` 引用 `.resx` 的写法不受影响，也不会被 Lingua 接管。

## 验证过什么

`MASA.Lingua.SDK.Maui` 是普通 `net8.0;net10.0` 双目标类库，在没有 MAUI 工作负载的环境里编译，`dotnet pack` 能打出包。
自动化测试（`tests/MASA.Lingua.Tests/MauiClientTests.cs`，10 条，对着进程内的 Lingua 服务）覆盖：

- 初始化后三个平台语种都落盘并可取词，占位符、语种切换、缺失 key、未发布语种回退到平台默认语种都正确；
- 第二次同步为「无变化」，模拟重启后不重复下载（ETag 已持久化）；
- 缓存文件被删除后，下一次同步自动补回（不会因 manifest 304 跳过补下载）；
- 语种下载中途失败时同步报错，`AvailableCultures` 不暴露还没落盘的语种，重试成功后才更新；
- 服务端不可达时 `InitializeAsync` / `TrySyncAsync` 不抛异常、缓存继续可用，`SyncAsync` 抛出；
- `Seed` 只在没有缓存时生效，并拒绝路径穿越的语种名；
- 项目未发布时不抛异常；
- 即使误开 `EnableMissingKeyTelemetry`，客户端也不发任何 POST（缺词上报已移除的回归守卫）；
- `AddLinguaMaui` 注册的 `LinguaLocalizer`、`IStringLocalizer`、`IStringLocalizer<T>`、通知器可用，默认缓存目录在本地应用数据目录下。

**没有验证**（这台机器没有 `maui` 工作负载，没法编译和运行 MAUI 应用）：

- 第 3、4、5 节里 MAUI 特有的代码：`App.xaml.cs`、`OnResume`、XAML 中 `{Binding [order.submitBtn], Source=...}` 的索引器绑定写法（key 里带 `.`）、`FileSystem.OpenAppPackageFileAsync`、`Resources/Raw` 打包；
- 真机 / 模拟器上的缓存目录位置和 HTTPS 访问。

首次在 MAUI 工程里使用时，如果索引器绑定在 key 含 `.` 时取不到值，可以把绑定改成绑定到一个封装属性，或告诉我，我再调整。
