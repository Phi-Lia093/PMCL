# AGENT.md — PMCL

## 1. 项目身份

- 名称：PMCL（**P**hilia093's **M**ine**C**raft **L**auncher）
- 目标：基于 C# / .NET 与 Avalonia 的现代 Minecraft Java 版启动器。
- 当前能力：原版版本列表 / 下载安装 / 启动、Microsoft 正版登录（含静默恢复）、离线登录、按版本独立配置 Java / 内存 / 窗口 / 登录方式 / 自动进服 / JVM 参数 / 游戏参数 / 启动前命令、版本重命名 / 删除 / 补全 / 导出启动脚本、全局游戏目录与语言占位、现代深色 UI。
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
│  ├─ GameVersionSettings.cs     # 每个已安装版本的启动设置
│  ├─ LauncherSettings.cs        # 全局设置 + versionSettings 字典
│  ├─ VersionLoginMode.cs        # 当前账户 / Microsoft / 离线
│  └─ SavedMicrosoftAccount.cs   # accounts.json 中的账户摘要
├─ Services/
│  ├─ AppPaths.cs                # %APPDATA%\PMCL 路径集中管理
│  ├─ AuthenticationService.cs   # Microsoft 登录/静默恢复/离线会话/登出
│  ├─ CommandLineSplitter.cs     # JVM / 游戏参数分词
│  ├─ GameLaunchCoordinator.cs   # 安装、补全、按版本启动、脚本导出
│  ├─ MinecraftService.cs        # CmlLib 封装 + 版本文件夹重命名/删除
│  ├─ SettingsService.cs         # settings.json 读写、规范化与旧设置迁移
│  └─ StoragePickerService.cs    # 文件夹 / Java / 脚本保存对话框
├─ ViewModels/
│  ├─ MainViewModel.cs           # 外壳：导航、页面实例、初始化、版本刷新
│  ├─ LauncherSession.cs         # 共享状态：账户、已安装版本、进度、错误
│  ├─ HomeViewModel.cs           # 主页（仅已安装版本启动 / 进入版本设置）
│  ├─ VersionsViewModel.cs       # 版本库（搜索、选择、安装）
│  ├─ AccountViewModel.cs        # Microsoft / 离线登录、已存账户切换、登出
│  ├─ VersionSettingsViewModel.cs# 版本设置页（自动保存、文件操作、脚本导出）
│  ├─ SettingsViewModel.cs       # 全局设置页（游戏目录、语言占位）
│  └─ VersionItemViewModel.cs    # 列表项（筛选用）
└─ Views/
   ├─ MainWindow.axaml(.cs)      # 侧边导航 + 页头 + 内容宿主 + 全局遮罩
   ├─ HomeView.axaml(.cs)
   ├─ VersionsView.axaml(.cs)
   ├─ AccountView.axaml(.cs)
   ├─ VersionSettingsView.axaml(.cs)
   └─ SettingsView.axaml(.cs)
```

## 5. 运行时数据

- 数据根目录：`%APPDATA%\PMCL`（`AppPaths.DataDirectory`）
- 设置：`%APPDATA%\PMCL\settings.json`
  - 全局字段：`gameDirectory`、`language`、`lastVersion`、`offlineUsername`、`lastAccountKind`
  - 版本字段：`versionSettings.<版本名>` 保存 Java、内存、窗口、登录方式、自动进服、JVM / 游戏参数、启动前命令
  - 旧版全局 `javaPath` / 内存 / 窗口字段会在首次加载时迁移到最后选择的版本
- 微软账户缓存：`%APPDATA%\PMCL\accounts.json`
- 日志目录预留：`%APPDATA%\PMCL\logs`
- 默认游戏目录：`MinecraftPath.GetOSDefaultPath()`（通常为 `%APPDATA%\.minecraft`）

关闭窗口时由 `App.axaml.cs` 的 `ShutdownRequested` 先 `MainViewModel.FlushPendingSettings()`（提交版本设置页的防抖修改），再保存设置。页面切换时也会 Flush，避免未落盘的编辑丢失。

## 6. 架构约定

1. **组合根**：`App.OnFrameworkInitializationCompleted` 手工构造 `AppPaths → SettingsService → LauncherSession → AuthenticationService → MinecraftService → StoragePickerService → MainViewModel`，不使用 DI 容器。
2. **单一共享状态**：所有页面绑定同一个 `LauncherSession`；全局忙碌/进度/错误由 `BeginBusy` / `EndBusy` / `SetStatus` / `SetError` 驱动。
3. **页面导航**：`MainViewModel.CurrentPage` 是 `ViewModelBase`，通过 `ContentControl` + `ViewLocator` 渲染。
   - `PMCL.ViewModels.HomeViewModel` → `PMCL.Views.HomeView`（依赖 `ViewModel` → `View` 的命名替换，新增页面必须遵守）。
   - 版本设置页不是侧边栏页面：主页选择已安装版本后由 `MainViewModel.OpenVersionSettings` 打开；打开期间主页导航保持高亮。
4. **回调解耦**：子页面通过构造函数接收 `Func<Task>`（如 `RefreshVersionsAsync`）或 `Action`，不直接引用 `MainViewModel`。
5. **异步与 UI 线程**：CmlLib 的阻塞/交互调用（尤其 Microsoft 登录）放在 `Task.Run` 中；UI 线程只负责状态更新。登录窗口不能在 UI 线程打开。
6. **绑定**：`x:DataType` + 编译绑定；属性名 / 命令名改动必须同步 XAML，编译期即可发现拼写错误。
7. **样式**：颜色与控件样式集中在 `App.axaml`。XAML 使用 `StaticResource` 画刷与样式类：
   - 画刷：`AppBackgroundBrush`、`SidebarBrush`、`AccentBrush`、`SuccessBrush`、`DangerBrush` 等
   - 类：`card`、`h1`、`h2`、`muted`、`label`、`primary`、`secondary`、`ghost`、`danger`、`icon`、`nav`、`nav.active`
   - 不要在页面里重复定义全局颜色；页面特有的渐变 / 半透明色可以内联。
8. **Avalonia 12 注意**：`TextBox` 使用 `PlaceholderText`（`Watermark` 已废弃）；`ListBox` 没有 `HorizontalContentAlignment`，需在 `ListBoxItem` 样式中设置。
9. **语言**：界面文案使用简体中文；代码注释与标识符使用英文。

## 7. 关键流程

- **启动**：`MainWindow.Opened` → `MainViewModel.InitializeAsync()` → 读取已存账户 → `RefreshVersionsAsync()`（同时重建 `Session.InstalledVersions`）→ `RestoreAccountAsync()`（仅当 `LastAccountKind` 为 Microsoft/Offline）。
- **主页版本选择**：ComboBox 只绑定 `LauncherSession.InstalledVersions`；`ApplyVersions` / `EnsureInstalledSelection` 保证主页只会选中已安装版本。
- **安装 / 补全**：`GameLaunchCoordinator` → `MinecraftService.InstallAsync`，通过 `IProgress<InstallerProgressChangedEventArgs>` / `IProgress<ByteProgress>` 更新 `Session.Progress` 与 `StatusMessage`；补全复用同一流程。
- **启动游戏**：取 `GameVersionSettings` → 按 `LoginMode` 解析账户（当前账户 / Microsoft / 版本离线用户名）→ 校验 Java、内存、服务器 → 安装缺失文件 → `MinecraftService.BuildProcessAsync`（内存、窗口、JVM / 游戏参数、自动进服）→ 执行启动前命令（`cmd.exe`，5 分钟超时，非 0 退出码中止）→ 启动 Java 进程。
- **版本设置持久化**：编辑字段后 500ms 防抖写入 `settings.json`，页面切换 / 关闭窗口 / 点击保存时立即 Flush；显示 `SaveState`。
- **版本文件操作**：重命名会移动版本文件夹并同步 json 的 `id`、同名 jar、子版本 `inheritsFrom`，同时迁移 `versionSettings` 键；删除只移除版本目录与专属设置，保留共享存档 / 截图。
- **导出启动脚本**：用与真实启动相同的 CmlLib 构建结果生成 `.bat`，包含 Java 路径、鉴权参数、JVM / 游戏参数、自动进服参数和启动前命令；脚本含登录令牌，UI 已提示妥善保管。
- **微软登录**：`JELoginHandler.AuthenticateInteractively`（WebView2）或 `AuthenticateSilently`；账户缓存由 `JELoginHandlerBuilder.WithAccountManager(paths.AccountsFile)` 管理。

## 8. 修改指南

- 新增页面：在 `ViewModels/` 建 `XxxViewModel : ViewModelBase`，在 `Views/` 建同名 `XxxView`，在 `MainViewModel` 建属性与导航命令，并补充侧边栏按钮。
- 新增**全局**设置：加到 `LauncherSettings`，在 `SettingsViewModel` 暴露属性并保存；只放真正跨版本的值。
- 新增**版本**设置：加到 `GameVersionSettings`，在 `VersionSettingsViewModel` 暴露属性、`SaveNow` 中写回模型；不要放回全局设置。
- 修改 CmlLib API 用法后务必检查包版本（`dotnet list package`），不同版本的事件参数成员可能不同（例如 `ExtraJvmArguments` 需要 `MArgument`，不是裸字符串）。
- 提交前至少运行一次 `dotnet build`，并启动程序确认主页、版本库、账户、全局设置、版本设置五个页面与版本列表 / 登录流程无异常。
- 涉及版本文件操作时，用临时游戏目录验证重命名 / 删除，至少确认 json `id`、jar 名称、`inheritsFrom` 与 `versionSettings` 键同步。

## 9. 后续路线（Roadmap）

- [ ] 安装进度百分比与下载速度显示
- [x] 启动参数自定义（JVM 参数、游戏参数、启动前命令）
- [ ] 日志查看器（`%APPDATA%\PMCL\logs`）
- [ ] 多账户管理与账户切换体验优化
- [ ] Forge / Fabric / NeoForge 模组加载器安装
- [ ] 多语言（中 / 英）与主题切换
- [ ] 自动更新与发布流水线

## 10. 已知约束

- 仅支持 Windows（TFM、`cmd.exe` 启动前命令与 WebView2 登录限制）。
- 原版启动器定位：不包含模组管理、整合包市场等功能。
- 离线账户只能用于单人和局域网，不能进入正版服务器。
- 版本重命名不支持“仅修改大小写”，且会重写该版本 json（保留结构但会格式化）；依赖外部硬编码旧版本名的第三方文件需自行调整。
- 导出的启动脚本包含当前登录令牌，令牌过期后需要重新导出。
