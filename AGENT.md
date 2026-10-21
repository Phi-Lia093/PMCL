# AGENT.md — PMCL

## 1. 项目身份

- 名称：PMCL（**P**hilia093's **M**ine**C**raft **L**auncher）
- 目标：基于 C# / .NET 与 Avalonia 的现代 Minecraft Java 版启动器。
- 当前能力：原版版本列表 / 下载安装 / 启动、Microsoft 正版登录（含静默恢复）、离线登录、Java 与内存 / 窗口 / 游戏目录设置、现代深色 UI。
- 当前版本：`0.1.0`（见 `PMCL.csproj`）。

## 2. 技术栈

| 组件 | 版本 / 说明 |
| --- | --- |
| .NET | `net10.0-windows`（**必须保留 Windows TFM**，CmlLib 的 WebView2 登录依赖它） |
| UI | Avalonia `12.1.4` + Fluent 主题 + Inter 字体 |
| MVVM | CommunityToolkit.Mvvm `8.4.2`（`[ObservableProperty]` 使用 **partial property** 写法） |
| Minecraft | CmlLib.Core `4.1.0`、CmlLib.Core.Auth.Microsoft `3.4.0` |
| 输出 | `WinExe`，程序集名 `PMCL` |

## 3. 常用命令（Windows / PowerShell）

```powershell
# 构建（仓库根目录 D:\PMCL）
dotnet build D:\PMCL\PMCL.csproj

# 运行
dotnet run --project D:\PMCL\PMCL.csproj

# 仅启动已构建的程序做冒烟测试
D:\PMCL\bin\Debug\net10.0-windows\PMCL.exe
```

目前没有测试项目；以 `dotnet build` 零警告 + 手工冒烟测试作为验收标准。

## 4. 目录结构

```
D:\PMCL
├─ App.axaml / App.axaml.cs      # 主题资源、服务组装、启动初始化
├─ Program.cs                    # Avalonia AppBuilder 入口
├─ ViewLocator.cs                # ViewModel -> View 约定映射
├─ PMCL.csproj
├─ Models/
│  ├─ AccountKind.cs             # Microsoft / Offline
│  ├─ AccountProfile.cs          # 当前账户 UI 模型
│  ├─ GameVersion.cs             # 版本（Name/Type/ReleaseTime/IsInstalled/IsRelease）
│  ├─ LauncherSettings.cs        # 持久化设置
│  └─ SavedMicrosoftAccount.cs   # accounts.json 中的账户摘要
├─ Services/
│  ├─ AppPaths.cs                # %APPDATA%\PMCL 路径集中管理
│  ├─ AuthenticationService.cs   # Microsoft 登录/静默恢复/离线会话/登出
│  ├─ GameLaunchCoordinator.cs   # 安装、构建进程、启动的编排
│  ├─ MinecraftService.cs        # CmlLib 封装（版本、安装、Java 检测）
│  ├─ SettingsService.cs         # settings.json 读写
│  └─ StoragePickerService.cs    # 文件夹选择对话框
├─ ViewModels/
│  ├─ MainViewModel.cs           # 外壳：导航、页面实例、初始化、版本刷新
│  ├─ LauncherSession.cs         # 共享状态：账户、版本、进度、错误、设置
│  ├─ HomeViewModel.cs           # 主页（启动/安装当前版本）
│  ├─ VersionsViewModel.cs       # 版本库（搜索、选择、安装）
│  ├─ AccountViewModel.cs        # Microsoft / 离线登录、已存账户切换、登出
│  ├─ SettingsViewModel.cs       # 设置页
│  └─ VersionItemViewModel.cs    # 列表项（筛选用）
└─ Views/
   ├─ MainWindow.axaml(.cs)      # 侧边导航 + 页头 + 内容宿主 + 全局遮罩
   ├─ HomeView.axaml(.cs)
   ├─ VersionsView.axaml(.cs)
   ├─ AccountView.axaml(.cs)
   └─ SettingsView.axaml(.cs)
```

## 5. 运行时数据

- 数据根目录：`%APPDATA%\PMCL`（`AppPaths.DataDirectory`）
- 设置：`%APPDATA%\PMCL\settings.json`
- 微软账户缓存：`%APPDATA%\PMCL\accounts.json`
- 日志目录预留：`%APPDATA%\PMCL\logs`
- 默认游戏目录：`MinecraftPath.GetOSDefaultPath()`（通常为 `%APPDATA%\.minecraft`）

关闭窗口时由 `App.axaml.cs` 的 `ShutdownRequested` 保存设置。

## 6. 架构约定

1. **组合根**：`App.OnFrameworkInitializationCompleted` 手工构造 `AppPaths → SettingsService → LauncherSession → AuthenticationService → MinecraftService → StoragePickerService → MainViewModel`，不使用 DI 容器。
2. **单一共享状态**：所有页面绑定同一个 `LauncherSession`；全局忙碌/进度/错误由 `BeginBusy` / `EndBusy` / `SetStatus` / `SetError` 驱动。
3. **页面导航**：`MainViewModel.CurrentPage` 是 `ViewModelBase`，通过 `ContentControl` + `ViewLocator` 渲染。
   - `PMCL.ViewModels.HomeViewModel` → `PMCL.Views.HomeView`（依赖 `ViewModel` → `View` 的命名替换，新增页面必须遵守）。
4. **回调解耦**：子页面通过构造函数接收 `Func<Task>`（如 `RefreshVersionsAsync`）或 `Action`，不直接引用 `MainViewModel`。
5. **异步与 UI 线程**：CmlLib 的阻塞/交互调用（尤其 Microsoft 登录）放在 `Task.Run` 中；UI 线程只负责状态更新。登录窗口不能在 UI 线程打开。
6. **绑定**：`x:DataType` + 编译绑定；属性名 / 命令名改动必须同步 XAML，编译期即可发现拼写错误。
7. **样式**：颜色与控件样式集中在 `App.axaml`。XAML 使用 `StaticResource` 画刷与样式类：
   - 画刷：`AppBackgroundBrush`、`SidebarBrush`、`AccentBrush`、`SuccessBrush`、`DangerBrush` 等
   - 类：`card`、`h1`、`h2`、`muted`、`label`、`primary`、`secondary`、`ghost`、`icon`、`nav`、`nav.active`
   - 不要在页面里重复定义全局颜色；页面特有的渐变 / 半透明色可以内联。
8. **Avalonia 12 注意**：`TextBox` 使用 `PlaceholderText`（`Watermark` 已废弃）；`ListBox` 没有 `HorizontalContentAlignment`，需在 `ListBoxItem` 样式中设置。
9. **语言**：界面文案使用简体中文；代码注释与标识符使用英文。

## 7. 关键流程

- **启动**：`MainWindow.Opened` → `MainViewModel.InitializeAsync()` → 读取已存账户 → `RefreshVersionsAsync()` → `RestoreAccountAsync()`（仅当 `LastAccountKind` 为 Microsoft/Offline）。
- **安装**：`GameLaunchCoordinator` → `MinecraftService.InstallAsync`，通过 `IProgress<InstallerProgressChangedEventArgs>` / `IProgress<ByteProgress>` 更新 `Session.Progress` 与 `StatusMessage`。
- **启动游戏**：确认已登录 + 已选版本 → 安装缺失文件 → `BuildProcessAsync` → 启动进程并记录状态。
- **微软登录**：`JELoginHandler.AuthenticateInteractively`（WebView2）或 `AuthenticateSilently`；账户缓存由 `JELoginHandlerBuilder.WithAccountManager(paths.AccountsFile)` 管理。

## 8. 修改指南

- 新增页面：在 `ViewModels/` 建 `XxxViewModel : ViewModelBase`，在 `Views/` 建同名 `XxxView`，在 `MainViewModel` 建属性与导航命令，并补充侧边栏按钮。
- 新增持久化设置：加到 `LauncherSettings`，在 `SettingsViewModel` 暴露属性，保存仍走 `SettingsService`。
- 修改 CmlLib API 用法后务必检查包版本（`dotnet list package`），不同版本的事件参数成员可能不同。
- 提交前至少运行一次 `dotnet build`，并启动程序确认主窗口、四个页面、版本列表与登录流程无异常。

## 9. 后续路线（Roadmap）

- [ ] 安装进度百分比与下载速度显示
- [ ] 启动参数自定义（JVM 参数、游戏参数）
- [ ] 日志查看器（`%APPDATA%\PMCL\logs`）
- [ ] 多账户管理与账户切换体验优化
- [ ] Forge / Fabric / NeoForge 模组加载器安装
- [ ] 多语言（中 / 英）与主题切换
- [ ] 自动更新与发布流水线

## 10. 已知约束

- 仅支持 Windows（TFM 与 WebView2 登录限制）。
- 原版启动器定位：不包含模组管理、整合包市场等功能。
- 离线账户只能用于单人和局域网，不能进入正版服务器。
