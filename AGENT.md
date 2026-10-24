# AGENT.md — PMCL

## 1. 项目身份

- 名称：PMCL（**P**hilia093's **M**ine**C**raft **L**auncher）
- 目标：基于 C# / .NET 与 Avalonia 的现代 Minecraft Java 版启动器。
- 当前能力：官方 / BMCLAPI 下载源切换、原版版本列表 / 下载安装 / 启动、下载百分比与实时速度、Forge / Fabric / NeoForge / OptiFine 模组加载器自动下载安装与互斥管理、版本内模组列表 / 启用 / 禁用 / 删除、Modrinth 模组搜索与下载（按 Minecraft 版本与加载器自动过滤兼容性、自动安装必需前置）、多账户管理（Microsoft / 离线）、Microsoft 设备码系统浏览器登录（含静默恢复与令牌缓存）、账户切换 / 删除、按版本独立配置 Java / 内存 / 窗口 / 登录方式 / 自动进服 / JVM 参数 / 游戏参数 / 启动前命令、强制版本隔离（存档 / 截图 / 模组 / 配置 / 资源包按版本隔离，libraries/assets/runtime 共享）、版本重命名 / 删除 / 补全 / 导出启动脚本、全局游戏目录与多语言（简体中文 / English，JSON 词条）、现代深色 UI。
- 当前版本：`0.1.0`（见 `PMCL.csproj`）。

## 2. 技术栈

| 组件 | 版本 / 说明 |
| --- | --- |
| .NET | `net10.0`（跨平台；Microsoft 登录使用系统浏览器，不再依赖 WebView2） |
| UI | Avalonia `12.1.4` + Fluent 主题 + Inter 字体 |
| MVVM | CommunityToolkit.Mvvm `8.4.2`（`[ObservableProperty]` 使用 **partial property** 写法） |
| Minecraft | CmlLib.Core `4.1.0`、CmlLib.Core.Installer.Forge `2.0.0`（Forge / NeoForge 安装引擎） |
| 输出 | Windows `WinExe` / 其他平台 `Exe`，程序集名 `PMCL` |

## 3. 常用命令（Windows / PowerShell）

```powershell
# 构建（仓库根目录 D:\PMCL）
dotnet build D:\PMCL\PMCL.csproj

# 运行
dotnet run --project D:\PMCL\PMCL.csproj

# 仅启动已构建的程序做冒烟测试（会打开 GUI；开发/自动测试时不要运行）
D:\PMCL\bin\Debug\net10.0\PMCL.exe

# 无头测试：控制台 Harness，不创建 Avalonia 窗口，也不启动游戏
dotnet run --project D:\PMCL\bin\Harness\Harness.csproj --nologo
```

开发与测试优先使用无头 Harness；`dotnet build`、Harness、目录操作均不会弹出启动器窗口。不要在自动测试中执行带 GUI 的 `dotnet run --project PMCL.csproj`。

## 4. 目录结构

```
D:\PMCL
├─ App.axaml / App.axaml.cs      # 主题资源、服务组装、启动初始化
├─ Program.cs                    # Avalonia AppBuilder 入口
├─ ViewLocator.cs                # ViewModel -> View 约定映射
├─ PMCL.csproj
├─ Assets/i18n/
│  ├─ zh-CN.json                 # 简体中文词条（默认语言）
│  └─ en-US.json                 # English 词条（键必须与 zh-CN 完全一致）
├─ Localization/
│  ├─ LocalizationService.cs     # 语言词条加载、缺失回退、语言切换事件
│  ├─ L.cs                       # 代码侧 `L.Tr` / `L.Format` 快捷入口
│  └─ TrExtension.cs             # XAML `{loc:Tr Key.Name}` 标记扩展
├─ Models/
│  ├─ AccountKind.cs             # Microsoft / Offline
│  ├─ AccountProfile.cs          # 当前账户 UI 模型
│  ├─ DownloadSourceKind.cs      # 官方源 / BMCLAPI 镜像
│  ├─ GameVersion.cs             # 版本（Name/Type/ReleaseTime/IsInstalled/加载器元数据）
│  ├─ GameVersionSettings.cs     # 每个已安装版本的启动设置
│  ├─ LauncherSettings.cs        # 全局设置 + versionSettings 字典
│  ├─ MicrosoftDeviceLoginChallenge.cs # 设备码、授权页与轮询有效期
│  ├─ ModLoaderKind.cs           # 原版 / Fabric / Forge / NeoForge / OptiFine
│  ├─ ModLoaderMetadata.cs       # 版本目录内 pmcl-loader.json 的模型
│  ├─ SavedAccount.cs            # 多账户模型（Microsoft / 离线、令牌缓存）
│  ├─ VersionLoginMode.cs        # 当前账户 / Microsoft / 离线
├─ Services/
│  ├─ AccountStore.cs            # accounts.json 多账户持久化/旧账户迁移
│  ├─ AppPaths.cs                # %APPDATA%\PMCL 路径集中管理
│  ├─ AuthenticationService.cs   # OAuth 设备码 → Xbox → Minecraft 登录/刷新/离线会话
│  ├─ CommandLineSplitter.cs     # JVM / 游戏参数分词
│  ├─ MicrosoftOAuthClient.cs    # Microsoft OAuth 设备码/刷新令牌
│  ├─ SystemBrowserService.cs    # 使用系统默认浏览器打开登录页
│  ├─ DownloadSourceRouter.cs    # HTTP 层官方/BMCLAPI 路由与官方回退
│  ├─ DownloadSpeedTracker.cs    # 下载速度平滑与字节格式化
│  ├─ GameLaunchCoordinator.cs   # 安装、补全、加载器互斥安装/移除、按版本启动、脚本导出
│  ├─ MinecraftService.cs        # CmlLib 封装 + 加载器安装 + 版本文件夹重命名/删除
│  ├─ ModLoaderDetector.cs       # pmcl-loader.json / 版本名加载器检测
│  ├─ ModLoaderService.cs        # Fabric/Forge/NeoForge/OptiFine 目录与安装
│  ├─ ModService.cs              # 版本 mods 扫描、.jar/.jar.disabled 切换、删除
│  ├─ ModrinthService.cs         # Modrinth 搜索、兼容版本选择、依赖递归下载
│  ├─ SettingsService.cs         # settings.json 读写、规范化与旧设置迁移
│  ├─ VersionIsolationService.cs # 强制版本目录、旧共享数据导入与加载器切换数据合并
│  └─ StoragePickerService.cs    # 文件夹 / Java / 脚本保存对话框
├─ ViewModels/
│  ├─ MainViewModel.cs           # 外壳：导航、页面实例、初始化、版本刷新
│  ├─ LauncherSession.cs         # 共享状态：账户、已安装版本、进度、错误
│  ├─ HomeViewModel.cs           # 主页（仅已安装版本启动 / 进入版本设置）
│  ├─ VersionsViewModel.cs       # 版本库（搜索、选择、打开加载器选择界面）
│  ├─ ModLoaderSelectorViewModel.cs # 共享加载器/版本选择、Java、互斥提示
│  ├─ ModLoaderChoiceViewModel.cs   # 加载器卡片 + 版本下拉
│  ├─ ModItemViewModel.cs        # 本地模组行（启用 / 禁用 / 删除确认）
│  ├─ ModrinthProjectItemViewModel.cs # Modrinth 搜索结果行与安装命令
│  ├─ AccountViewModel.cs        # 多账户列表、设备码登录、切换 / 删除 / 自动恢复
│  ├─ VersionSettingsViewModel.cs# 版本设置页（启动参数、模组管理、Modrinth）
│  ├─ SettingsViewModel.cs       # 全局设置页（游戏目录、语言切换）
│  └─ VersionItemViewModel.cs    # 列表项（筛选用）
└─ Views/
   ├─ MainWindow.axaml(.cs)      # 侧边导航 + 页头 + 内容宿主 + 全局遮罩
   ├─ HomeView.axaml(.cs)
   ├─ VersionsView.axaml(.cs)
   ├─ ModLoaderSelectorView.axaml(.cs) # 版本库/版本设置共用的加载器选择弹层
   ├─ AccountView.axaml(.cs)
   ├─ VersionSettingsView.axaml(.cs)
   └─ SettingsView.axaml(.cs)
```

## 5. 运行时数据

- 数据根目录：`%APPDATA%\PMCL`（`AppPaths.DataDirectory`）
- 设置：`%APPDATA%\PMCL\settings.json`
  - 全局字段：`gameDirectory`、`language`、`lastVersion`、`offlineUsername`、`lastAccountKind`、`lastAccountId`、`downloadSource`（`official` / `bmclApi`，默认 `official`）
  - 版本字段：`versionSettings.<版本名>` 保存 Java、内存、窗口、登录方式、自动进服、JVM / 游戏参数、启动前命令
  - 旧版全局 `javaPath` / 内存 / 窗口字段会在首次加载时迁移到最后选择的版本；已移除的 `isolateVersionsByDefault` / `isolationEnabled` 残留字段会被忽略
- 账户：`%APPDATA%\PMCL\accounts.json`
  - `accounts[]` 保存账户类型、用户名、UUID、Xbox Gamertag / XUID、刷新令牌、Minecraft 令牌与过期时间、最近使用时间
  - 旧 CmlLib 账户文件自动迁移并保留为 `accounts.legacy.json`；损坏文件会备份为 `accounts.corrupt-*.json`
- 加载器元数据：每个加载器版本目录内的 `pmcl-loader.json`（`loader`、`loaderVersion`、`minecraftVersion`）
  - 新安装会写入；旧 Forge / Fabric / OptiFine / NeoForge 文件夹会通过版本名与 json `inheritsFrom` 回退识别
  - 重命名原版版本时会同步更新子加载器的 `minecraftVersion`
- 多账户文件：`%APPDATA%\PMCL\accounts.json`（旧文件迁移备份为 `accounts.legacy.json`）
- 日志目录预留：`%APPDATA%\PMCL\logs`
- 默认游戏目录：`MinecraftPath.GetOSDefaultPath()`（通常为 `%APPDATA%\.minecraft`）
- 版本隔离（强制）：`--gameDir` 始终指向 `<gameDirectory>\versions\<版本名>`，该文件夹内的 `saves`、`screenshots`、`mods`、`config`、`defaultconfigs`、`resourcepacks`、`shaderpacks`、`logs`、`crash-reports`、`options.txt`、`servers.dat` 均为版本私有；`libraries`、`assets`、`runtime` 与 `versions` 元数据仍来自共享根目录，不会复制。没有关闭隔离的开关，避免不同版本的模组互相污染。旧共享布局可在版本设置点击「导入旧共享数据」把 `saves` / `screenshots` / `mods` / `config` / `resourcepacks` / `shaderpacks` / `options.txt` 复制进来（只复制、不覆盖）。
- 模组文件：每个版本的 `mods` 目录存放 `.jar`（启用）与 `.jar.disabled`（禁用）；启用 / 禁用只重命名文件，不修改 jar 内容。Modrinth 下载的文件也写入同一目录。
- 加载器切换隔离数据：安装 / 更换 / 移除加载器时，`VersionIsolationService.MergeVersionData` 会先把旧加载器（或原版）版本文件夹中的用户数据复制到新目标目录，再删除旧版本文件夹，避免互斥清理误删存档与模组。

关闭窗口时由 `App.axaml.cs` 的 `ShutdownRequested` 先 `MainViewModel.FlushPendingSettings()`（提交版本设置页的防抖修改），再保存设置。页面切换时也会 Flush，避免未落盘的编辑丢失。

## 6. 架构约定

1. **组合根**：`App.OnFrameworkInitializationCompleted` 手工构造 `AppPaths → SettingsService → LauncherSession → AuthenticationService → MinecraftService → StoragePickerService → MainViewModel`，不使用 DI 容器；`MainViewModel` 再创建 `ModLoaderService`、`ModService`（本地模组文件）与 `ModrinthService`（HTTP API）并注入版本设置页。
2. **单一共享状态**：所有页面绑定同一个 `LauncherSession`；全局忙碌/进度/错误由 `BeginBusy` / `EndBusy` / `SetStatus` / `SetError` 驱动。
3. **页面导航**：`MainViewModel.CurrentPage` 是 `ViewModelBase`，通过 `ContentControl` + `ViewLocator` 渲染。
   - `PMCL.ViewModels.HomeViewModel` → `PMCL.Views.HomeView`（依赖 `ViewModel` → `View` 的命名替换，新增页面必须遵守）。
   - 版本设置页不是侧边栏页面：主页选择已安装版本后由 `MainViewModel.OpenVersionSettings` 打开；打开期间主页导航保持高亮。
4. **回调解耦**：子页面通过构造函数接收 `Func<Task>`（如 `RefreshVersionsAsync`）或 `Action`，不直接引用 `MainViewModel`。
5. **异步与 UI 线程**：Microsoft 设备码登录的 HTTP 链与轮询使用 `async/await`（`ConfigureAwait(false)`），浏览器打开只做 `Process.Start`；CmlLib 的阻塞安装调用仍放在异步流程中，UI 线程只负责状态更新。
6. **绑定**：`x:DataType` + 编译绑定；属性名 / 命令名改动必须同步 XAML，编译期即可发现拼写错误。
7. **样式**：颜色与控件样式集中在 `App.axaml`。XAML 使用 `StaticResource` 画刷与样式类：
   - 画刷：`AppBackgroundBrush`、`SidebarBrush`、`AccentBrush`、`SuccessBrush`、`DangerBrush` 等
   - 类：`card`、`h1`、`h2`、`muted`、`label`、`primary`、`secondary`、`ghost`、`danger`、`icon`、`nav`、`nav.active`
   - 不要在页面里重复定义全局颜色；页面特有的渐变 / 半透明色可以内联。
8. **Avalonia 12 注意**：`TextBox` 使用 `PlaceholderText`（`Watermark` 已废弃）；`ListBox` 没有 `HorizontalContentAlignment`，需在 `ListBoxItem` 样式中设置。
9. **语言 / 国际化**：界面文案一律通过 JSON 词条渲染，禁止在 C# 或 XAML 里写死中文（或英文）界面字符串。
   - XAML：`{loc:Tr Section.Key}`（`xmlns:loc="using:PMCL.Localization"`）。
   - 代码：`L.Tr("Section.Key")`，带占位符用 `L.Format("Section.Key", ...)`，`{0}` 为参数位。
   - 词条文件：`Assets/i18n/zh-CN.json`（默认）与 `en-US.json`；新增键必须同时写入两份文件，键名保持完全一致，缺失键会回退默认语言再回退键名本身。
   - 键名按功能前缀分组：`App`、`Nav`、`Home`、`Versions`、`Version`、`Settings`、`Account`、`Loader`、`Mod`、`Modrinth`（`MR`）、`Launch`、`Auth`、`OAuth`、`MC`、`ML`、`Storage`、`Browser`、`Status`、`Tray`、`Page`。
   - 语言名称本身（如 `简体中文`）不翻译，保持母语写法，方便用户找到自己的语言。
   - 代码注释与标识符仍使用英文。

## 7. 关键流程

- **启动**：`MainWindow.Opened` → `MainViewModel.InitializeAsync()` → `Account.ReloadSavedAccounts()` → `RefreshVersionsAsync()`（同时重建 `Session.InstalledVersions`）→ `Account.RestoreLastAccountAsync()`（按 `LastAccountId` 恢复；Microsoft 优先复用缓存令牌，过期后静默刷新）。
- **主页版本选择**：ComboBox 只绑定 `LauncherSession.InstalledVersions`；`ApplyVersions` / `EnsureInstalledSelection` 保证主页只会选中已安装版本。
- **安装 / 补全**：原版安装由 `GameLaunchCoordinator` → `MinecraftService.InstallAsync`，通过 `IProgress<InstallerProgressChangedEventArgs>` / `IProgress<ByteProgress>` 更新 `Session.Progress`、`ProgressPercentText`、`DownloadBytesText` 与 `DownloadSpeedText`；加载器版本的补全会重新执行对应加载器安装。
- **下载源**：全局设置默认「官方源」，元数据与文件优先走 Mojang / Fabric / Forge / NeoForge / OptiFine 官方地址，官方请求失败时才尝试镜像回退；选择「BMCLAPI 镜像」时由 `DownloadSourceRouter` 在 HTTP 层改写官方 URL，镜像失败自动回退到原官方 URL，不会强依赖 BMCLAPI。
- **模组加载器**：版本库的「下载」不再直接安装，而是打开 `ModLoaderSelectorView`；可选原版 / Fabric / Forge / NeoForge / OptiFine，并选择加载器版本。
  - Fabric：官方 Fabric Meta（镜像模式用 BMCLAPI Fabric Meta）获取 profile json，写入版本目录后由 CmlLib 安装继承库与资源。
  - Forge：Forge 官方 Maven 元数据 / installer（镜像模式用 BMCLAPI Maven），`ForgeV12Installer` 执行 installer profile 与 processors。
  - NeoForge：NeoForge 官方 Maven 元数据 / installer（镜像模式用 BMCLAPI 镜像），同一 V12 安装引擎执行 processors。
  - OptiFine：默认走 OptiFine 官方下载页 `adloadx` → `downloadx` 获取安装包；镜像模式先用 BMCLAPI，失败回退官方；内置 Java 8 兼容 headless helper 调用 `optifine.Installer.doInstall`。
  - 互斥：同一原版版本只保留一个加载器；安装/更换成功后，`GameLaunchCoordinator` 会先把旧加载器的用户数据合并到新加载器目录，再删除该原版下其他加载器版本，并把当前启动设置复制到新加载器。
  - 版本设置页可添加 / 更换 / 移除加载器；移除只删除加载器版本并回到原版，设置会尽量写回原版。
- **模组管理**：版本设置页的「已安装模组」扫描该版本 `mods` 目录，只列出 `.jar` 与 `.jar.disabled`；启用 / 禁用通过 `ModService.SetEnabled` 重命名，删除前在行内二次确认。刷新、搜索过滤与「显示已禁用」都在 `VersionSettingsViewModel` 内完成。
- **Modrinth**：`ModrinthService.SearchAsync` 使用 `project_type:mod` + `versions:<Minecraft 版本>` + `categories:<fabric|forge|neoforge>` facets 搜索；安装时用 `/project/{id}/version?game_versions=...&loaders=...` 取最新兼容文件，递归下载 `dependency_type=required` 的前置（支持固定 `version_id` 与按项目重新选择兼容版本），同名文件跳过；原版与 OptiFine 不提供入口。UI 会显示安装数量、跳过数量与未解析依赖警告。
- **启动游戏**：取 `GameVersionSettings` → 按 `LoginMode` 解析账户（当前账户 / Microsoft / 版本离线用户名）→ 校验 Java、内存、服务器 → 安装缺失文件 → `MinecraftService.BuildProcessAsync`（内存、窗口、JVM / 游戏参数、自动进服；`MLaunchOption.Path` 始终指向版本文件夹，启动器本身仍使用共享根，因此 libraries/assets/runtime 不重复下载）→ 执行启动前命令（Windows 用 `cmd.exe`，其他平台用 `/bin/sh`；5 分钟超时，非 0 退出码中止；工作目录与 `{gameDir}` 使用版本目录）→ 启动 Java 进程。
- **版本设置持久化**：编辑字段后 500ms 防抖写入 `settings.json`，页面切换 / 关闭窗口 / 点击保存时立即 Flush；显示 `SaveState`。
- **版本文件操作**：重命名会移动版本文件夹并同步 json 的 `id`、同名 jar、子版本 `inheritsFrom`，同时迁移 `versionSettings` 键；版本文件夹（含存档 / 模组）随重命名一起移动。删除会移除整个版本目录与专属设置，包括该版本私有的存档 / 截图 / 模组；共享 libraries/assets/runtime 与其它版本不受影响。
- **导出启动脚本**：用与真实启动相同的 CmlLib 构建结果生成 `.bat`，包含 Java 路径、鉴权参数、JVM / 游戏参数、自动进服参数和启动前命令；脚本含登录令牌，UI 已提示妥善保管。
- **多账户**：`AccountStore` 持久化 `accounts.json`，支持 Microsoft / 离线账户、`LastAccountId` 自动恢复、`LastAccess` 排序、同 UUID / 同名去重、删除账户；旧 CmlLib 账户文件会迁移并备份为 `accounts.legacy.json`。
- **微软登录**：`AuthenticationService.BeginMicrosoftDeviceLoginAsync` 用官方 Minecraft 客户端 ID 请求设备码（`login.live.com/oauth20_connect.srf`）→ `SystemBrowserService` 打开带 `code` 参数的 `https://www.microsoft.com/link` 授权页（页面支持时自动填入短代码）→ `MicrosoftOAuthClient.PollDeviceCodeAsync` 按 `interval` 轮询 `oauth20_token.srf`，处理 `authorization_pending` / `slow_down` / 过期 / 取消 → 拿到 Microsoft OAuth 令牌 → Xbox Live 用户认证 → XSTS → Minecraft Services 登录 → 拉取 Java 档案；刷新令牌与 Minecraft 令牌缓存用于静默恢复。用户无需粘贴回调链接或令牌，全程不加载 WebView2。

## 8. 修改指南

- 新增页面：在 `ViewModels/` 建 `XxxViewModel : ViewModelBase`，在 `Views/` 建同名 `XxxView`，在 `MainViewModel` 建属性与导航命令，并补充侧边栏按钮。
- 新增**全局**设置：加到 `LauncherSettings`，在 `SettingsViewModel` 暴露属性并保存；只放真正跨版本的值。
- 新增**版本**设置：加到 `GameVersionSettings`，在 `VersionSettingsViewModel` 暴露属性、`SaveNow` 中写回模型；不要放回全局设置。
- 版本路径相关逻辑统一走 `VersionIsolationService` / `MinecraftService.GetGameDirectory`，不要在页面里直接拼接 `.minecraft\saves`；版本始终隔离，所有页面必须使用版本目录。
- 模组文件操作统一走 `ModService`；不要手动拼接 `.disabled` 后缀，也不要绕过 `ModService.Scan` 直接枚举目录，否则会漏掉禁用文件或把非 jar 文件当成模组。
- Modrinth 相关逻辑统一走 `ModrinthService`；兼容性判断必须同时限定 Minecraft 版本与加载器，依赖递归要带去重集合，避免循环依赖导致无限下载。
- 修改 CmlLib API 用法后务必检查包版本（`dotnet list package`），不同版本的事件参数成员可能不同（例如 `ExtraJvmArguments` 需要 `MArgument`，不是裸字符串）。
- 提交前至少运行一次 `dotnet build`，并启动程序确认主页、版本库、账户、全局设置、版本设置五个页面与版本列表 / 登录流程无异常。
- 涉及版本文件操作时，用临时游戏目录验证重命名 / 删除，至少确认 json `id`、jar 名称、`inheritsFrom` 与 `versionSettings` 键同步。
- 修改 Microsoft 登录时注意：官方 Minecraft 客户端 ID 只支持设备码与固定 desktop 回调，不能把回调重定向到 `localhost`；只有接入自建 Entra 应用（自行注册 loopback redirect）才适合做完全无输入的浏览器回跳。
- 修改模组管理 / Modrinth 时，Harness 已覆盖 `ModService` 的重命名/删除、facet 兼容性、依赖递归与 `VersionSettingsViewModel` 的列表/安装流程；新增网络逻辑应继续使用 `FakeModrinthHandler` 保证测试离线可重复。

## 9. 后续路线（Roadmap）

- [x] 安装进度百分比与下载速度显示
- [x] 官方 / 镜像下载源选择（官方源不依赖 BMCLAPI）
- [x] 启动参数自定义（JVM 参数、游戏参数、启动前命令）
- [x] Forge / Fabric / NeoForge / OptiFine 模组加载器安装
- [x] 版本隔离（强制；存档 / 截图 / 模组 / 配置 / 资源包按版本隔离，libraries/assets/runtime 共享）
- [x] 版本模组管理（扫描 mods、启用 / 禁用 `.jar.disabled`、删除）
- [x] Modrinth 模组搜索 / 下载（兼容性过滤、必需前置自动安装）
- [ ] 模组更新检查与整合包导入
- [ ] 日志查看器（`%APPDATA%\PMCL\logs`）
- [x] 多账户管理与账户切换体验优化（Microsoft 系统浏览器设备码登录、离线账户、令牌缓存/刷新）
- [ ] 多语言（中 / 英）与主题切换
- [ ] 自动更新与发布流水线

## 10. 已知约束

- 主项目已改为跨平台 `net10.0`，不再依赖 WebView2；Windows 专属体验仍包括原生暗色标题栏与 `.bat` 启动脚本导出。`CmlLib.Core.Installer.Forge` 的 processors 在 Windows 上可能短暂显示 Java 控制台。
- Microsoft 官方 Minecraft 客户端只允许固定的 `https://login.live.com/oauth20_desktop.srf` 回调，无法直接把授权结果重定向到本机端口；因此主流程改用官方客户端支持的 Device Code Flow。用户在系统浏览器输入/确认短代码后，启动器自动轮询并完成登录，不需要复制回调链接或 token。这不是内嵌浏览器，也不会读取浏览器 Cookie；短代码等同于一次性登录凭据，请勿分享。
- 账户文件中的 Microsoft 刷新令牌以明文保存在 `%APPDATA%\PMCL\accounts.json`，与旧版启动器一致；请勿分享该文件。
- 启动器当前只负责加载器安装、启动与模组文件管理；不包含整合包市场、模组自动更新等功能。Modrinth 集成仅实现搜索、兼容版本下载与必需前置解析。
- 离线账户只能用于单人和局域网，不能进入正版服务器。
- 版本重命名不支持“仅修改大小写”，且会重写该版本 json（保留结构但会格式化）；依赖外部硬编码旧版本名的第三方文件需自行调整。
- 模组加载器安装依赖网络与官方 Maven / Fabric Meta / OptiFine 官方页；BMCLAPI 仅为可选加速源，官方源可独立完成全部安装。Forge 自动安装支持 1.12.2+，NeoForge 支持官方目录中存在的版本（通常 1.20.2+），OptiFine 仅列出官方稳定 HD U 版本。
- Forge / NeoForge 的安装器 processors 由 CmlLib 安装引擎启动 Java；正式安装时可能出现短暂的 Java 控制台窗口，这是第三方安装引擎行为，自动测试不会真正执行 processors。
- Forge / NeoForge 的 `install_profile.json` 只负责安装安装器与 processor 依赖；最终 `version.json` 可能额外引用启动所需的库（旧 Forge 如 1.12.2 的 launchwrapper、ASM、Scala、Log4j）。安装流程在 installer 完成后必须再调用一次 CmlLib 的版本安装，启动前还必须校验 classpath，缺失时自动补全，否则 JVM 会以“找不到主类”立即退出。
- 启动 Java 由版本元数据的 `javaVersion` 决定：1.17、1.18–1.20.4、1.20.5+ 分别使用对应 Mojang 运行时；没有有效 `javaVersion` 的旧版本（如 1.6.1 及更早的 Alpha / Beta）固定使用 `jre-legacy`（Java 8）。运行时不存在时由 CmlLib 自动下载，不能用系统里最新的 Java 代替旧版运行时。
- 同一原版版本只保留一个模组加载器；安装/更换会删除该原版下其他加载器版本，因此 OptiFine 当前不能与 Forge/Fabric/NeoForge 叠加。切换加载器会先复制旧加载器的存档 / 模组 / 配置到新加载器目录，不会随旧文件夹删除。
- 版本隔离只拆分游戏数据，`libraries`、`assets`、`runtime` 与版本元数据始终共享；不同版本不会各自下载一份依赖，因此删除共享目录中的资源会影响所有版本。
- 旧版共享布局升级到强制隔离布局时不会自动移动文件（避免把存档复制多份）；需要在版本设置点击「导入旧共享数据」手动迁移。删除版本会连同其私有存档 / 截图 / 模组一起删除，UI 已提示。
- Modrinth 需要访问 `api.modrinth.com` 与 `cdn.modrinth.com`，并带有符合要求的 User-Agent；仅 Fabric / Forge / NeoForge 版本可使用，原版与 OptiFine 不提供入口。下载只跳过同名文件，不会自动删除旧版本模组；可选依赖不会自动安装。
- 导出的启动脚本包含当前登录令牌，令牌过期后需要重新导出。
