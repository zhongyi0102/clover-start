# 四叶草启动面板 (CloverStart)

轻量级 Windows 11 开始菜单替代面板：任务栏左下角悬浮一个四叶草按钮，点开就是一屏按分类整理好的全部软件。

## 使用

- 点击任务栏里的四叶草按钮，或按 `Ctrl + Alt + 空格`，打开 / 收起面板
- 按钮是任务栏里的一个独立按钮（44×48，原生样式、悬停高亮），位于应用按钮组末尾并自动跟随；不会遮挡任何图标
- 面板窗口可以像普通软件一样**拖动**（按住顶部标题区拖动）和**调整大小**（拖四边或四角），位置和尺寸保存在 `config\layout.json`
- 右上角三个窗口按钮：**─ 最小化**（收起面板）/ **□ 最大化·还原**（铺满整个屏幕，再点一次还原）/ **✕ 关闭**（收起面板）
- 左侧是分类（常用 / 全部 / 办公 / 开发 / 网络通讯 / 影音娱乐 / 系统工具 / 工具/命令行 / 其他），点一下切换；「常用」按使用次数自动收集（最多 12 个），「游戏」已并入「影音娱乐」
- 应用图标按**使用频率**排序：从面板启动次数越多越靠前，次数相同按最近使用，没用过的按名称；数据保存在 `config\usage.json`（删掉即可重置）
- 应用磁贴可以**拖动调整顺序**（像桌面图标一样）：拖动一个磁贴放到另一个磁贴上即可换位，顺序按分类保存到 `config\order.json`，删掉该文件恢复按使用频率排序
- 顶部搜索框：输入即过滤，支持中文、英文，以及**拼音**——全拼（`weixin` → 微信）和首字母（`wx` → 微信）都行
- **键盘操作**：面板弹出即聚焦搜索框；`↑ ↓ ← →` 在磁贴间移动选中（选中项有绿色边框），`Home`/`End` 跳到第一个/最后一个，`Enter` 启动选中项（未选中时启动第一个），`Esc` 关闭。在搜索框里按 `←` `→` 仍是移动光标，只有 `↑` `↓` `Enter` 会接管
- 点击应用图标直接启动；`Esc` 或点击面板外自动收起
- 「常用」里右键应用磁贴 → **从常用移除**（清掉该应用的启动次数记录，它仍保留在原分类和「全部」里）；这个菜单项只在「常用」分类里出现
- **所有分类**里右键应用磁贴 → **打开文件所在位置**：在资源管理器里打开该应用所在目录并选中它。`.lnk` 会先解析出真实目标再定位（不是定位到开始菜单里那个快捷方式）；文件已被卸载但目录还在时直接打开目录；两者都不存在时给出提示
- **所有分类**里右键应用磁贴 → **移到分类 →**：手工把应用归到你认为对的分类。结果存在 `config\category-overrides.json`，**优先级高于自动分类规则**，重新扫描后依然保持；已经手工指定过的应用，菜单里会多一项「恢复自动分类」
- 左侧分类下方的**任务栏**区域列出当前打开的所有窗口（图标 + 标题），点一下切换到该窗口；当前前台窗口高亮，每 1.5 秒自动刷新
- 任务栏里的窗口项右键 → 显示在任务栏（把按钮放到左下角）；左下角的按钮右键可关闭窗口或从任务栏移除，窗口关掉后按钮自动消失

### 右键四叶草按钮的菜单

| 菜单项 | 说明 |
|---|---|
| 打开面板 | 同点击按钮 |
| 重新扫描应用 | 后台重新扫描开始菜单与便携软件 |
| 立即清理桌面快捷方式 | 立即把桌面 `.lnk` 移到 `desktop-backup\`（即使关了自动开关，手动清理仍可用） |
| **还原桌面快捷方式** | 把 `desktop-backup\` 里的 `.lnk` 放回桌面。重名时加「-还原N」后缀，**绝不覆盖**；成功的从备份移除，失败的只记 `cleanup.log` 不重试 |
| **启用迷你任务栏** | 勾选项，默认开。关掉后左下角不再显示窗口按钮，`显示在任务栏` 菜单也失效 |
| **自动移走桌面快捷方式** | 勾选项，**默认关**。首次勾选会弹一次说明确认，点取消则不勾选。关掉后不再自动移动桌面 `.lnk` |
| **允许隐藏系统任务栏** | 勾选项，默认开。关掉会立即恢复任务栏，并禁用下面一项 |
| 隐藏系统任务栏 | 勾选项，默认关。勾上后整条系统任务栏隐藏，左下角只留「开始」+ 四叶草 |
| 开机自启 | 勾选项 |
| 退出 | 退出前会自动还原系统状态并清除运行标记 |

### 配置开关（config\settings.json）

| 键 | 默认 | 作用 |
|---|---|---|
| `hideTaskbar` | `false` | 隐藏系统任务栏（当前状态） |
| `taskbarHiding` | `true` | 是否**允许**隐藏系统任务栏这个功能 |
| `miniTaskbar` | `true` | 迷你任务栏 |
| `desktopAutoClean` | **`false`** | 自动移走桌面快捷方式（本轮从 `true` 改为 `false`） |
| `desktopAutoCleanNoticeShown` | `false` | 是否已展示过首次开启确认（展示过就不再弹） |
| `hoverReveal` | `true` | 悬停唤出（全屏时鼠标移到按钮位置临时显示） |
| `fullscreenDetect` | `true` | 全屏检测（全屏时自动隐藏悬浮按钮） |
| `safeMode` | `false` | 强制安全模式 |
| `hiddenCategories` | `["系统工具"]` | 隐藏的分类 |

> **共 9 个键**：7 个功能开关（`taskbarHiding` / `miniTaskbar` / `desktopAutoClean` / `hoverReveal` / `fullscreenDetect` / `safeMode`）+
> `hideTaskbar`（状态）+ `desktopAutoCleanNoticeShown`（提示标记）+ `hiddenCategories`（分类过滤）。
> 出问题可以单独关掉对应功能，不用卸载。
>
> **兼容性**：老配置里如果显式写了 `"desktopAutoClean": true`，升级后**仍保持 `true`**，不会被新默认值覆盖；
> 只有没写这一项的配置才采用新默认 `false`。

## 自动特性

- **新软件自动加入**：监听开始菜单（用户 + 公共）里的 `.lnk`，安装软件后约 2.5 秒内重新扫描并刷新面板（多次文件事件会合并成一次）；另外每 15 分钟兜底刷新一次。
- **启动时先读缓存、再后台刷新**：面板打开时直接用 `config\apps.json` 渲染（秒开，不等待扫描）；**启动过程本身不触发扫描**，只有开始菜单发生变化或 15 分钟兜底到点时才在后台扫描，扫完自动刷新界面。
- **扫描在后台线程跑**：开始菜单遍历和便携软件目录递归都不占界面线程；同一时刻只会有一个扫描在跑。
- **图标磁盘缓存**：提取过的图标按「路径 + 文件修改时间 + 大小」存进 `config\iconcache\`（PNG），下次启动直接读盘；程序升级或换图标后缓存自动失效。超过 90 天未用或数量超过 800 个时自动清理旧的。
- **绿色/便携软件也能识别**：除开始菜单快捷方式外，还扫描各磁盘的软件目录（`Software` / `Portable` / `Programs` / `Apps` / `Tools` / `绿色软件` / `软件` / `应用`，最多 5 层），把解压即用的软件也收进面板（名字优先取 exe 的"文件说明"）。安装包、卸载器、更新器、日志/缓存目录自动排除。想额外扫描别的目录，新建 `config\portable-roots.json`：`{ "roots": ["D:\\Software\\Portable", "E:\\Tools"] }`（写这个文件后就不再自动推断目录）。
- **桌面不留快捷方式（默认关闭）**：默认**不**动桌面。用户手动勾选「自动移走桌面快捷方式」后（首次会弹一次说明确认），桌面（含公共桌面）上出现的 `.lnk` 会被移到 `desktop-backup\`（按时间命名）；被移走的快捷方式若指向面板里没有的程序，会自动登记到 `config\manual-apps.json`，仍能启动。
  - **一键还原**：右键菜单「还原桌面快捷方式」把备份放回桌面，重名自动加「-还原N」后缀，绝不覆盖
  - **备份永不自动删除**：超过 200 个只在 `error.log` 写一条 WARN（同一次运行只写一次），不删任何文件

## 分类规则

### 优先级（高 → 低）

| 顺序 | 来源 | 说明 |
|---|---|---|
| 1 | **用户手工指定** | `config\category-overrides.json`，来自右键菜单「移到分类」。重新扫描后仍保持 |
| 2 | **开始菜单目录名** | 辅助提示，**不作主分类**。例如 `Anaconda3 (64-bit)`、`JetBrains` 这类目录会归到「开发」 |
| 3 | **应用名关键词** | 主分类依据，下表的规则按顺序匹配，命中即返回 |
| 4 | 兜底 | 都不匹配 → 「其他」 |

> 分类靠关键词枚举，**必然会有判错的**。与其不断堆规则，不如用右键「移到分类」纠正一次——结果优先级最高，之后不会再被自动规则改回去。

### 关键词规则（按匹配顺序）

| 分类 | 命中关键词（节选） |
|---|---|
| **工具/命令行** | `prompt`、`command line`、`console`、`terminal`、`bash`、`shell`、`powershell`、`cmd`、`driver`、`module docs`、`reset `（开头）、`guide`/`manual`/`help`/`readme`/`faq`/`documentation`/`docs` |
| **系统工具** | Windows 自带管理/辅助工具（`file explorer`、`task manager`、`registry editor`、`event viewer`、`narrator`、`magnify`…）；`7-zip`/`winrar`/`sharex`/`snip`/`disk`；打印机扫描仪（`hp`/`canon`/`epson`/`打印机`/`扫描仪`/`传真`） |
| **办公** | `word`/`excel`/`powerpoint`/`wps`/`office`/`notion`/`obsidian`/`onenote`/`飞书`/`foxit`/`pdf` |
| **开发** | `code`/`studio`/`python`/`git`/`node`/`sql`/`java`/`docker`/`postman`/`pycharm`/`idea`/`anaconda`/`mongo`/`dbeaver`/`ollama`/`wsl` 等 |
| **网络通讯** | `chrome`/`edge`/`firefox`/`telegram`/`wechat`/`微信`/`qq`/`抖音`/`钉钉`/`迅雷`/`clash`/`karing`/`localsend`/`向日葵` 等 |
| **影音娱乐** | `steam`/`game`/`游戏`/`player`/`vlc`/`potplayer`/`music`/`bilibili`/`剪映`/`mineradio`/`faststone` 等 |
| **其他** | 以上都不命中 |

**注意**：`Git` 归「开发」（版本控制工具），而 `Git Bash` / `Git CMD` 是命令行窗口，归「工具/命令行」。
「工具/命令行」这类平时用不到的东西单独分出去，是为了让「开发」分类里不再混着十几个命令行窗口。

### 覆盖表文件

```json
{
  "version": 1,
  "updated": "2026-10-21 18:00:00",
  "overrides": {
    "C:\\ProgramData\\...\\HP Scan.lnk": "系统工具",
    "D:\\Software\\Geek\\geek.exe": "系统工具"
  }
}
```

- key 是**应用路径**（唯一且稳定），值是想归入的分类
- 路径**大小写不敏感**
- 文件损坏 / 读不出时**回退为空表**（等于全部走默认规则），不阻断启动
- 删掉该文件即恢复全部自动分类

## 目录结构

```
clover-start\
  app\CloverStart.exe        主程序（.NET Framework 4.8 WPF，免安装）
  app\clover.log             运行日志（面板开关、启动应用等）
  config\apps.json           扫描生成的软件清单（含分类）
  config\manual-apps.json    手动补充 / 来自桌面快捷方式的软件
  config\settings.json       开关与隐藏分类
  config\usage.json          启动次数与最近使用时间
  config\order.json          手动拖拽排序
  config\layout.json         面板位置与尺寸
  config\iconcache\          应用图标磁盘缓存（可安全删除，会自动重建）
  config\backup\yyyyMMdd\    每日配置备份（只留最近 7 天）
  config\error.log           异常/警告日志
  config\system-state.json   未还原的系统状态标记（存在=需要还原；详见「紧急恢复」）
  config\crash-guard.json    崩溃计数与安全模式标记
  res\clover.ico, clover.png 四叶草图标
  src\core\                  纯逻辑层（零 WPF 依赖，可单独编译成 DLL）
  src\ui\                    界面层（依赖 WPF）
  tests\                     最小测试
  tools\make-pinyin.ps1      生成拼音表
  build.ps1                  编译（csc.exe，无需 MSBuild）
  install.ps1                创建开机自启并启动
  desktop-backup\            被移走的桌面快捷方式（可恢复）
```

## 源码结构

源码分**两层**，依赖方向是单向的：`ui` 依赖 `core`，`core` 不依赖任何界面代码。

```
src\core\    纯逻辑层：扫描、配置、看门狗、崩溃保护、拼音…
             零 WPF 依赖，可脱离 WPF 单独编译成 DLL
src\ui\      界面层：窗口、控件、图标、日志实现
             依赖 WPF，依赖 core
```

`build.ps1` 会**先用「只含非 WPF 程序集」的引用单独编译 core**。只要有人在 core 里用了 WPF 类型，编译立刻失败并指出原因——这是**编译器强制**的分层约束，不是靠自觉。

**好处**：core 层可以脱离界面单测；界面重构不会波及底层逻辑；崩溃保护、扫描这些关键代码能被独立编译验证。

> 为什么不在构建时真的产出两个 DLL？那会让发布多带一个 `Clover.Core.dll`，破坏「单文件 exe」这个核心特性。所以 core 的独立编译只用于**验证**，产物用完即删。

### `src\core\`（纯逻辑，零 WPF）

| 文件 | 负责 |
|---|---|
| `Native.cs` | P/Invoke 声明与 Win32 结构体 |
| `Log.cs` | 日志抽象（core 只调它，实现由 ui 注入） |
| `JsonIO.cs` | 原子写 + `.bak` 回退 + 每日备份 |
| `Settings.cs` | `settings.json` 统一读写（保留未知字段） |
| `Catalog.cs` | 软件清单模型、分类规则、排序 |
| `CategoryOverrides.cs` | 用户手工指定的「应用 → 分类」覆盖表（优先级最高） |
| `UsageStore.cs` | 启动次数与最近使用时间 |
| `AppScan.cs` | 扫描流程编排（开始菜单 → 死链接清理 → 便携软件） |
| `NativeScanner.cs` | 遍历开始菜单目录、猜分类、清理死链接 |
| `PortableScanner.cs` | 便携软件识别（目录递归、主程序挑选、重名去重） |
| `ScanService.cs` | 后台扫描调度（单飞 + 完成后回界面线程） |
| `Pinyin.cs` | 拼音匹配（全拼 / 首字母） |
| `PinyinData.cs` | 汉字拼音表（自动生成，勿手改） |
| `SystemState.cs` | `system-state.json` 的读写（崩溃后能靠它还原） |
| `TaskbarVisibility.cs` | 系统状态改动的唯一入口 + `RestoreSystemState()` |
| `ResetAll.cs` | 一键回滚：撤销全部系统改动（8 个步骤，逐项报告） |
| `CrashGuard.cs` | 崩溃计数、安全模式判定、稳定清除 |
| `Guard.cs` | 看门狗（`--guard` 模式） |
| `HeartbeatFile.cs` | 心跳文件读写（看门狗据此判卡死） |
| `SafeCall.cs` | UIAutomation/COM 调用的异步包装（界面线程不阻塞） |
| `FullscreenWatch.cs` | 全屏判定（含多显示器） |
| `Version.cs` | 编译时自动生成（版本号 + 构建时间），版本号取自根目录 `VERSION` |

### `src\ui\`（界面层，依赖 WPF）

**面板本体**

| 文件 | 负责 |
|---|---|
| `PanelWindow.cs` | 窗口生命周期、显示/隐藏、拖拽改尺寸、窗口布局存取、启动应用 |
| `PanelWindow.Ui.cs` | 界面骨架 `BuildUi` 与通用小控件（图标按钮、字形） |
| `PanelWindow.Nav.cs` | 左侧分类、搜索过滤、应用磁贴渲染、拖拽排序、键盘选中导航、右键菜单 |
| `PanelWindow.Tasks.cs` | 左侧“任务栏”区域（当前窗口列表、右键菜单、定时刷新） |
| `PanelWindow.Data.cs` | 目录/配置加载保存、刷新、对外接口、设置应用 |

**窗口与外观**

| 文件 | 负责 |
|---|---|
| `TriggerWindow.cs` | 四叶草悬浮按钮、右键菜单、开机自启 |
| `StartButtonWindow.cs` | 任务栏隐藏时的「开始」按钮 |
| `MiniTaskbarWindow.cs` | 左下角迷你任务栏 |
| `OverlayGate.cs` | 悬浮按钮的全屏隐藏 / 悬停唤出统一调度 |
| `TaskbarGeometry.cs` | 任务栏所在边/厚度、开始按钮定位 |
| `TaskList.cs` | 枚举顶层窗口、切换/关闭 |
| `IconCache.cs` | 图标内存 + 磁盘缓存、过期清理 |
| `AppIcons.cs` | 图标获取（把 `ImageSource` 挡在 core 之外） |
| `Ui.cs` | 配色、字体、图片加载 |
| `Heartbeat.cs` | 心跳定时器（文件读写委托给 core 的 `HeartbeatFile`） |

**基础设施**

| 文件 | 负责 |
|---|---|
| `Program.cs` | 入口、单实例、命令行（`--rescan`/`--show`/`--safe-mode`/`--restore-system`/`--reset-all`/`--guard`）、异常钩子接线、稳定计时、**Log 接线** |
| `ErrorLog.cs` | error.log（分级、去重、1MB 轮转）、三层未处理异常钩子 |
| `AppWatcher.cs` | 监听开始菜单/桌面变化、去抖、定时兜底、桌面清理 |
| `ResourceWatch.cs` | 内存/句柄自检与缓存清理 |
| `VirtualizingWrapPanel.cs` | 虚拟化面板（已实现，尚未接入） |

**测试**

| 文件 | 负责 |
|---|---|
| `tests\CatalogTest.cs` | 分类规则 / JsonIO / 每日备份 |
| `tests\CategoryOverrideTest.cs` | 分类覆盖表：优先级 / 重扫保持 / 损坏回退 |
| `tests\PinyinTest.cs` | 拼音匹配 |
| `tests\SettingsTest.cs` | 设置读写与开关联动 |
| `tests\CrashGuardTest.cs` | 崩溃计数与安全模式判定 |
| `tests\WatcherTest.cs` | 文件监听溢出兜底 / Dispose 泄漏 |
| `tests\VirtualPanelTest.cs` | 虚拟化面板几何与滚动计算 |
| `tests\DesktopRestoreTest.cs` | 桌面快捷方式还原（重名不覆盖） |
| `tests\OpenFolderTest.cs` | 「打开文件所在位置」路径解析 |
| `tests\FullscreenTest.cs` | 全屏检测（真建无边框全屏窗口） |
| `tests\TaskbarCreatedTest.cs` | TaskbarCreated 消息注册与接收 |
| `tests\ActiveRowTest.cs` | 任务栏行高亮判定（输入回调只读缓存后的纯逻辑） |
| `tests\AcceptanceTest.cs` | **验收测试**：真建面板 + JSON 证据报告 + 拒绝真实目录 |
| `tests\run-tests.ps1` | 一键编译并运行全部套件 |

## 健壮性

### 异常兜底范围

分四层，越靠后越兜底：

| 层级 | 位置 | 行为 |
|---|---|---|
| 1. 界面线程未处理异常 | `DispatcherUnhandledException` | 写日志（含完整堆栈）、**记录崩溃次数**、**还原系统状态**、弹提示，`e.Handled = true` 继续运行 |
| 2. Task 未观察异常 | `TaskScheduler.UnobservedTaskException` | 写日志并 `SetObserved()`，不让它升级成进程崩溃 |
| 3. 非界面线程未处理异常 | `AppDomain.UnhandledException` | 写日志、记录崩溃次数、**还原系统状态**（进程随后终止） |
| 4. 启动阶段 / 进程退出 | `Program.Main` try/catch、`ProcessExit` | 写日志、弹「启动失败 + 版本号」、还原系统状态 |

局部 catch 覆盖的位置：扫描全流程（`AppScan`/`NativeScanner`/`PortableScanner`/`ScanService`）、图标提取与缓存（`IconCache`）、图片加载（`Ui.LoadImage` 失败返回 null）、JSON 读写（`JsonIO`/`Settings`/`SystemState`/`CrashGuard`，读取失败回退 `.bak` 或默认值）、任务栏几何（`TaskbarGeometry`，失败保留估算值）、全屏检测（`FullscreenWatch`）、悬浮按钮轮询（`OverlayGate`）、面板渲染与刷新（`PanelWindow`）、文件监听回调（`AppWatcher`）、开始菜单点击（`StartButtonWindow`，分步执行且失败必恢复）、看门狗（`Guard`，异常绝不外抛）。

### 系统任务栏的恢复（含看门狗）

**隐藏任务栏的实现方式**：直接 `ShowWindow(Shell_TrayWnd, SW_HIDE)`。**不改自动隐藏设置、不改注册表**。

**改动的系统状态一共只有两处**：

| 操作 | 改了什么 | 何时发生 |
|---|---|---|
| 隐藏任务栏 | `Shell_TrayWnd` 可见性 | 勾选「隐藏系统任务栏」 |
| 透明化任务栏 | `WS_EX_LAYERED` + `SetLayeredWindowAttributes(alpha=1)` | 点左下角「开始」按钮时临时用，用完立刻恢复 |

两处都在动手**之前**先把原始状态落盘到 `config\system-state.json`，还原后删除：

```json
{"taskbarHidden":true,"taskbarLayered":false,"since":"2026-10-20 15:43:00","version":"2026.10.20.1543"}
```

> 文件存在 = 有未还原状态。这是**唯一权威记录**，不依赖内存变量。

**还原路径覆盖**（全部调用同一个 `RestoreSystemState()`）：

| 路径 | 触发 |
|---|---|
| 用户点「退出」 | 退出菜单 |
| 托盘/窗口关闭 | `Application.Exit` |
| 会话结束 | `Application.SessionEnding`（注销/关机） |
| 界面线程异常 | `DispatcherUnhandledException` |
| 非界面线程异常 | `AppDomain.UnhandledException` |
| Task 未观察异常 | `TaskScheduler.UnobservedTaskException` |
| 进程退出 | `AppDomain.ProcessExit` |
| 启动时 | 读 `system-state.json`，有残留先还原再启动 |
| **看门狗** | 见下 |

**看门狗（`--guard` 模式）**

主进程启动时会以无界面子进程方式拉起 `CloverStart.exe --guard <主进程PID> <目录>`。它做两件事：

**1. 等主进程退出 → 还原**

主进程退出后，如果 `system-state.json` 还有标记就还原，然后自己退出。**主进程被任务管理器强杀也能触发**（这是普通退出钩子做不到的）。

**2. 卡死检测（本轮新增）**

每秒检查主进程是否无响应，判定方式按优先级：

| 优先级 | 依据 | 说明 |
|---|---|---|
| a | `IsHungAppWindow(主窗口)` | 系统自己的"程序未响应"判定，最准确 |
| b | `config\heartbeat` 时间戳 | 主进程没有可检测窗口时，读界面线程每 2 秒写的心跳；超过 2 个周期（5 秒）没更新即视为卡死 |

- 连续 **10 秒**判定无响应、且 `system-state.json` 存在未还原状态时，执行还原并写 WARN
- **只还原任务栏，绝不杀主进程**
- 主进程恢复响应后，如果用户仍勾选隐藏，可以再隐藏，但**同一次运行最多自动重新隐藏 1 次**，避免来回抖动
  - 实现：看门狗还原后写 `config\guard-restored` 标记；主进程的定时器检测到该标记且用户仍勾选隐藏时，自动重新隐藏一次并**删除标记**（用完即失效）
  - 启动时会清理遗留标记，保证每次运行重新计数

**约束**：无界面、无常驻窗口、1 秒轮询、只读一个小文件；任何异常都不外抛。

**explorer 重启**：监听 `TaskbarCreated` 消息，资源管理器重启后重新应用隐藏设置并重新定位按钮，不报错、不进入崩溃循环。

### 紧急恢复（任务栏不见了怎么办）

按顺序试，**都不需要启动四叶草**：

1. **重启资源管理器**：`Ctrl + Shift + Esc` 打开任务管理器 → 「文件」→「运行新任务」→ 输入 `explorer.exe` → 确定。
   或任务管理器「详细信息」里找到 `explorer.exe` 右键「重新启动」。
2. **用程序自带的还原命令**（推荐，最干净）：
   ```
   CloverStart.exe --restore-system
   ```
   只还原系统状态然后退出，不启动界面。它会把 `Shell_TrayWnd` 显示回来、清掉 `system-state.json`。
3. **删掉标记文件**：删除 `config\system-state.json`。
   含义：告诉程序「没有遗留状态要还原」。**注意**：删了它不会让任务栏回来，只是不再自动还原；任务栏本身要用方法 1 或 2 恢复。
4. 实在不行：注销 / 重启一次系统。

> 最坏情况说明：如果进程被强杀**且**看门狗也被一起杀掉，任务栏会保持隐藏到下次启动四叶草——启动时的残留还原会把它放回来。

### 一键回滚（彻底卸载前用）

想彻底回到「从没装过四叶草」的状态，运行：

```
CloverStart.exe --reset-all                 全量回滚（连配置和缓存一起清）
CloverStart.exe --reset-all --keep-config   只撤销对系统的影响，保留配置与应用清单
```

和 `--restore-system` 的区别：

| 命令 | 范围 | 用途 |
|---|---|---|
| `--restore-system` | 只撤销**系统级状态**（任务栏可见性/透明样式） | 紧急恢复，任务栏不见了 |
| `--reset-all` | 系统状态 + 开机自启 + 看门狗进程 + 崩溃守卫 + 图标缓存 + 桌面备份 | 彻底卸载、准备移除程序 |

**回滚的 8 个步骤**（每步独立 try/catch，单步失败不影响后续）：

1. 恢复系统任务栏（可见性 + 撤掉 `WS_EX_LAYERED`）
2. 清除 `system-state.json`
3. 校验任务栏自动隐藏状态（只记录不改动，避免误改用户原有设置）
4. 移除开机自启快捷方式
5. 停止看门狗进程（**排除自身 PID**，只杀无窗口的 guard 进程）
6. 清除 `crash-guard.json`（注意顺序：先 `ClearSafeMode()` 再删文件，否则会被写回来）
7. 清理图标缓存（`--keep-config` 时跳过）
8. 清理桌面快捷方式备份（`--keep-config` 时跳过）

退出码：**0 = 全部成功**，非 0 = 有失败项（详见 `config\error.log`）。逐项结果同时写入日志。

> **为什么右键菜单里没有「一键回滚」入口**（有意为之）
>
> `--reset-all` 会删掉图标缓存和桌面备份，属于**破坏性操作**。放在右键菜单里，误点一次就丢数据，而确认弹窗又容易被习惯性点掉。
>
> 所以它**只作为命令行工具保留**：需要回滚的人一定是在做卸载/排查，会主动查文档；随手点菜单的人不该有机会碰到它。
>
> 如果以后要加，至少需要：二次确认 + 先自动备份配置到 `config\backup\` + 明确列出将被删除的项。

### 崩溃循环保护（安全模式）

记录每次异常退出到 `config\crash-guard.json`：

- **2 分钟内连续异常退出 3 次** → 下次启动进入安全模式
- 安全模式下自动关闭：隐藏系统任务栏、迷你任务栏、自动移走桌面快捷方式、全屏检测、悬停唤出
- 只保留面板和按钮，弹一次提示 + 写 `error.log`
- **稳定运行 5 分钟**后自动清除计数、解除安全模式

**异常退出的判定：运行标记法（本轮新增）**

靠"上次有没有留下运行标记"来判断，比只看异常钩子更可靠——**强杀、断电、被安全软件终止**这些钩子跑不到的情况也能识别：

| 时机 | 行为 |
|---|---|
| 启动时 | 先把 `running=true` 原子写入 `crash-guard.json`（同时记 `runStarted`） |
| 启动时发现上次残留 `running=true` | 记为一次异常退出（补记），然后开新一轮 |
| 异常钩子已经记过本轮 | **不重复计**（用独立的 `crashRecordedThisRun` 布尔标记去重，不靠时间戳比较） |
| 正常退出 / 用户点退出 / 会话结束 | 清除 `running` 标记 |
| 看门狗拉起的还原 | **不算**异常退出（那条路径不经过 `BeginRun`） |

> 注意：N 次异常退出需要 N+1 次启动才能累计——第一次启动是干净起点，不计。
> 时钟回拨防护保留（未来时间戳不计入）。

**心跳文件**：界面线程每 2 秒把 UTC ticks 写进 `config\heartbeat`，供看门狗在没有可检测窗口时判断卡死。正常退出时删除。

手动入口：

```
CloverStart.exe --safe-mode        强制本次进入安全模式
CloverStart.exe --restore-system   只还原系统状态然后退出
```

或在 `config\settings.json` 里设 `"safeMode": true`。

### 不拖慢系统的措施

| 项目 | 做法 |
|---|---|
| 全局钩子 | **没有使用** `WH_KEYBOARD_LL` / `WH_MOUSE_LL`。热键用 `RegisterHotKey`；悬停唤出用 150ms 轻量轮询（无钩子） |
| UIAutomation | **界面线程绝不同步等待**。`FindFirst` 需要消息泵，同步等会导致必然超时；现在改为后台线程异步探测，结果回到界面线程再应用，失败/超时都走几何估算值 |
| 无长期订阅 | 没有使用会长期订阅的 `AutomationEvent`，无需 `RemoveAllEventHandlers` |
| FileSystemWatcher | 处理 `Error` 事件（缓冲区溢出）→ 合并成一次全量扫描；回调只投递消息不干活；退出时 `Dispose` |
| 扫描限流 | 便携软件递归：深度 ≤ 5、目录 ≤ 8000、**文件 ≤ 20 万**、**单次超时 60 秒**（超限中止并写日志，已扫到的保留） |
| 资源自检 | 每 10 分钟记录内存/句柄（含峰值）；超过 **400 MB / 3000 句柄**写 WARN，并清理应用图标缓存 + 窗口图标缓存 + 强制 GC |
| 图标/GDI | 图标统一走 `IconCache`（内存 + 磁盘），`DestroyIcon` 在 `finally` 里保证执行；窗口列表图标按句柄缓存，进程变化自动失效 |

**定时器清单**：

| 定时器 | 间隔 | 用途 | 备注 |
|---|---|---|---|
| `OverlayGate` | 700ms / 150ms | 全屏检测 + 悬停唤出 | 仅全屏且开启悬停时才 150ms |
| `TriggerWindow` 跟随 | 2s | 跟随任务栏按钮组末尾 | |
| `TriggerWindow` 置顶 | 1.2s | 把按钮抬回任务栏之上 | 不可见时直接 return |
| `PanelWindow` 任务栏列表 | 1.5s | 刷新窗口列表 | 只在面板可见时刷新 |
| `AppWatcher` 去抖 | 2.5s | 合并文件变化事件 | 一次性，不常驻 |
| `AppWatcher` 兜底 | 15 分钟 | 全量扫描兜底 | 已从 5 分钟放宽 |
| `ResourceWatch` | 10 分钟 | 内存/句柄自检 | |
| 稳定计时 | 5 分钟 | 清除崩溃计数 | 一次性 |

### 会动用户文件的操作

- **只移动 `.lnk`**，不动其他类型
- 重名时加时间后缀（`时间戳-N_原名`），**绝不覆盖**
- 失败（权限不足、文件被占用）只记日志到 `desktop-backup\cleanup.log`，**不重试**（避免死循环）
- `desktop-backup` 上限 **200 个**，超出时删最旧的
- 公共桌面无权限时跳过该目录并记日志
- 所有写文件失败都不会抛到界面线程；磁盘满/只读时降级为只读运行（配置写失败只记 WARN）

### 内存与性能实测记录

这一节记录一次完整的内存排查，避免以后重复踩坑。

**发现并修复的真实泄漏（14.16 MB/分钟）**

`TriggerWindow.RequestTaskbarLayout()` 的异步回调里调用了 `Reposition()`，而 `Reposition()` 又会调用 `RequestTaskbarLayout()`，形成递归放大：每 2 秒产生一轮 UIAutomation 调用和闭包分配，实测 **14.16 MB/分钟**，挂机 8 小时会到 6.9 GB。

修复方式：把「应用布局」拆成独立的 `ApplyCachedLayout()`，回调只更新位置、不再回头触发探测；同时给探测加 5 秒间隔下限。

**修复后仍观察到的增长，经定位是 WPF 预热而非泄漏**

| 阶段 | 工作集 | 托管堆 | 说明 |
|---|---|---|---|
| 启动 | 101 MB | 4.3 MB | 基线 |
| 反复开关面板 20 轮 | 214 MB | 6.6 MB | 增长 |
| 继续开关 40 轮 | **214.6 MB** | 6.6 MB | **完全停滞** |
| 空闲 90 秒 | 103 MB | — | 稳定，涨 1 MB |

关键数据：**托管堆只有 6 MB，工作集 214 MB**，且从第 20 次起完全不再增长。这说明增长来自 WPF 透明窗口（`AllowsTransparency=true`）的渲染表面预热——首次显示会建立 D3D 表面缓存，建满后收敛。预热后再测，40 次开关只涨 3.7 MB（**单次 0.09 MB**）。

**同时修复的四处真实优化**（都是实打实的浪费，只是不是"泄漏"）：

1. 反复开关面板时不再重建 113 个磁贴 —— 加内容签名 `RenderAppsIfNeeded()`，内容没变就跳过
2. 窗口列表图标按句柄缓存（`TaskList`），进程变化自动失效 —— 原来每次枚举都重新 `CreateBitmapSourceFromHIcon`
3. 显示/隐藏动画对象复用，不再每次 `new DoubleAnimation` + 挂 `Completed` 委托
4. `RefreshTaskList` 加 800ms 最小间隔，避免 `ShowPanel` 与定时器叠加重复全量枚举

**教训**：用 `WorkingSet64` 单指标判断泄漏会误判。必须同时看**托管堆（`GC.GetTotalMemory`）**和**工作集**，两者差距大说明是原生/WPF 侧；还要区分「一次性预热」和「持续线性增长」——把开关次数拉到 40 次以上，看曲线是否收敛。

### 做一键回滚时发现并修掉的两个 bug

**1. 跨线程访问 WPF 对象（会持续写 WARN 日志）**

`ApplyCachedLayout()` 由 `SafeCall.RunAsync` 的**后台线程回调**调用，却直接设置 `Left`/`Top`——WPF 窗口属性只能在创建它的线程上改。表现是每次探测都写一条 `应用任务栏布局失败: 调用线程无法访问此对象，因为另一个线程拥有该对象`。

修复：方法入口检查 `Dispatcher.CheckAccess()`，不是 UI 线程就 `BeginInvoke` 切回去。

**2. `crash-guard.json` 删不掉**

原来先 `File.Delete` 再 `CrashGuard.ClearSafeMode()`，而 `ClearSafeMode()` 内部会**重新写入**该文件，于是删完又被写回来。

修复：调换顺序，先 `ClearSafeMode()` 再删除。

**顺带发现的坑**：`--reset-all` 里"停止看门狗进程"最初会**把执行回滚的进程自己也杀掉**——因为 `winexe` 的 `MainWindowHandle` 也是 `IntPtr.Zero`，用"无主窗口"作为判断条件会命中自身。必须显式排除 `Process.GetCurrentProcess().Id`。

### 关于列表虚拟化（评估过，暂不启用）

WPF 的 `WrapPanel` **不虚拟化**，磁贴是用 `tilePanel.Children.Add()` 逐个创建的实体控件。113 个磁贴约 565 个可视化元素。

已实现 `src\ui\VirtualizingWrapPanel.cs`（继承 `VirtualizingPanel` + `IScrollInfo`，只生成视口内的行），几何计算已通过 19 项测试，理论收益：113 项降到约 70 项（**省 38%**）。

**但实测后决定暂不启用**，理由是收益与风险不成比例：

| 项目 | 数据 |
|---|---|
| 首次渲染 80~113 个磁贴 | **146~175 ms**（启动时一次性，之后靠内容签名去重不再重建） |
| 面板淡入动画 | 150 ms（渲染与动画重叠，用户感知不到额外延迟） |
| 虚拟化预估收益 | 约 65 ms（175 → 110 ms） |
| 代价 | 拖拽排序、键盘选中、右键菜单三处都依赖 `tilePanel.Children` 索引，虚拟化后会错乱，需全部重写 |

**触发重新评估的条件**：单个分类的应用数超过 ~300 个，或渲染耗时超过 300 ms。届时直接用已备好的 `VirtualizingWrapPanel`，把 `RenderAppsCore` 改成 `ItemsControl` + `ItemTemplate` 即可。


### 其他

- **单实例**：重复双击 / 开机自启后再手动打开，只会通知已运行的实例弹出面板，不会出现第二个进程或第二个四叶草图标
- **配置防损坏**：所有 JSON 写入都是"临时文件 + 原子替换"，并保留 `.bak`；读取失败自动回退 `.bak`，再失败则用默认值
- **每日配置备份**：每天第一次写某个配置时，把当前内容存一份到 `config\backup\yyyyMMdd\`，同一天多次写只留最早那份；只保留最近 7 天
- **日志**：`config\error.log` 每条独立一行，带时间与级别（INFO/WARN/ERROR）；连续重复的内容合并成"共 N 次"；超过 1 MB 自动轮转成 `error.log.1`
- **自动刷新 + 死链接清理**：装/卸载软件后自动更新；扫描时剔除目标已不存在的死链接
- 诊断用命令行：`CloverStart.exe --rescan` 手动触发一次扫描；`--show` 直接打开面板；`--restore-system` 只还原系统状态；`--safe-mode` 强制安全模式

### 全屏自动隐藏

检测到全屏应用（`SHQueryUserNotificationState` 判全屏游戏/D3D/演示模式，或前台窗口无标题栏且铺满**它所在的那块显示器**）时，四叶草 / 开始 / 迷你任务栏按钮自动隐藏；鼠标移到按钮位置 0.1 秒内浮现，移开再隐藏，退出全屏自动恢复。桌面和普通最大化窗口不算全屏；多显示器按窗口所在显示器判断；热键始终可用。

### 任务栏几何定位与回退

按钮位置按三级顺序确定，失败时逐级退让：

| 级别 | 依据 | 失败条件 |
|---|---|---|
| 1 | `SystemParameters.WorkArea` 与屏幕尺寸比较，判断任务栏在哪条边、多厚 | 工作区等于全屏（任务栏自动隐藏/被隐藏） |
| 2 | 用 `Shell_TrayWnd` 窗口矩形推断边与厚度 | 找不到窗口、或矩形不合理（超出屏幕/厚度 > 80px） |
| 3 | 回退到底部 48px 的固定估算 | —（最终兜底，不会失败） |

在此之上，用 UIAutomation 找原生「开始」按钮的真实矩形（`Name = 开始 / Start`）做精确定位；**失败时保留上面算出的估算位置**，不会让按钮消失或跑到屏幕外。每次状态变化写一条日志（不刷屏）：

- 成功：`开始按钮定位: ok/workArea`
- 失败：`开始按钮定位失败，使用估算位置：<原因>（来源 <来源>）`

`TriggerWindow` 每 2 秒跟随任务栏应用按钮组末尾重新定位；拿不到任务栏矩形时回退到上面的估算位置，不会卡在旧坐标。

### 面板与按钮是否跟随开始按钮所在显示器

**不跟随，只认主显示器。** 具体表现：

| 项目 | 依据 | 多显示器下的行为 |
|---|---|---|
| 面板初始位置 | `SystemParameters.WorkArea`（主屏工作区） | 出现在主屏左下 |
| 面板还原位置 | `ClampToScreen()` 用 `PrimaryScreenWidth/Height` | 被限制在主屏范围内 |
| 面板最大化 | `SystemParameters.WorkArea` | 铺满主屏 |
| 四叶草按钮 / 开始按钮 / 迷你任务栏 | `TaskbarGeometry` 用 `PrimaryScreenWidth/Height` 与主屏 `WorkArea` | 定位在主屏任务栏 |
| 全屏检测 | `MonitorFromWindow`（按窗口所在显示器） | **唯一按实际显示器判断的部分** |

也就是说：任务栏在主屏时一切正常；如果任务栏被移到副屏，或主副屏缩放比例不同（混合 DPI），按钮和面板会出现错位。这是已知限制，本轮未改动（需要 per-monitor DPI 支持，改动面大且必须实机验证）。

## 测试

```
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

**十三个套件，共 312 个断言**，不依赖测试框架，直接用 `csc` 编成 console exe。

**单元测试**（不需要桌面会话）：

| 套件 | 断言数 | 覆盖 |
|---|---|---|
| `CatalogTest` | 76 | 分类规则（含「工具/命令行」、Windows 系统工具、HP 不再误判办公）、大小写、优先级、`HideCategories`、`ContainsName/Path`、`JsonIO` 往返、`.bak` 回退、每日备份与裁剪 |
| `CategoryOverrideTest` | 30 | 分类覆盖表：优先级高于关键词规则、重新加载后保持、路径大小写不敏感、**文件损坏时回退到默认规则**、跳过空 key/value、损坏后自愈 |
| `PinyinTest` | 37 | 单字拼音、首字母、全拼、中英混合、边界值、真实应用名覆盖率、性能 |
| `SettingsTest` | 24 | 九个键的默认值（含 `desktopAutoClean` 默认 `false`）、老配置显式 `true` 不被覆盖、首次确认标记读写、未知字段保留、损坏 JSON 回退 |
| `WatcherTest` | 5 | 事件风暴下监听链路、Error 处理器已挂、`Dispose` 后无泄漏回调 |
| `VirtualPanelTest` | 17 | 虚拟化行/列计算、视口范围、滚动边界钳制、ItemsControl 数据层 |

**集成测试**（链接完整源码，真建窗口 / 真发消息 / 真调 API）：

| 套件 | 断言数 | 覆盖 |
|---|---|---|
| `CrashGuardTest` | 45 | 崩溃计数、2 分钟窗口边界、安全模式阈值、时钟回拨防护；**运行标记法**：残留标记计数、与钩子计数去重、正常退出清除标记、重复 `EndRun` 幂等、N+1 次启动累计 N 次；**重新隐藏配额**：无标记不放行、首次放行、二次拒绝、消费后删标记、启动清理 |
| `DesktopRestoreTest` | 21 | 重名不覆盖（内容校验）、多轮还原互不覆盖、还原后从 backup 移除、文件被占用时失败不重试、只处理 `.lnk`、空/不存在目录安全返回 |
| `OpenFolderTest` | 20 | 「打开文件所在位置」路径解析：存在的文件走 `/select`、文件没了退回目录、目录也没了给提示、`.lnk` 解析真实目标、无效 `.lnk` 退回自身、null/空串安全 |
| `FullscreenTest` | 5 | 子进程建**真正的无边框全屏窗口** → 确认它成为前台窗口 → 断言 `IsFullscreen()` = true → 关掉后恢复 false |
| `TaskbarCreatedTest` | 6 | 消息号注册唯一性、与 `Native` 一致、子进程监听 → 父进程投递 → 确认收到 |
| `ActiveRowTest` | 14 | 任务栏行高亮判定：只有一行亮、前台句柄为 0 时不高亮任何行、切换后刚好翻转、重复判定幂等 |

**验收测试**（真实创建窗口，验证"界面确实画出来了"）：

| 套件 | 断言数 | 覆盖 |
|---|---|---|
| `AcceptanceTest` | 12 | **真实创建 `PanelWindow`** 并 Show → 检查控件树可遍历（约 100 个可视元素）、左侧分类栏有条目、应用磁贴已渲染、搜索框可输入、覆盖表在真实界面链路上生效、显示/隐藏不抛异常 |

`AcceptanceTest` 借鉴了两个做法（**只借鉴形态，不复制代码** —— 参考项目用的是与 MIT 不兼容的非商业许可）：

1. **输出 JSON 证据报告**，而不是只喊"通过"。报告含每项的实测数据（如"共 101 个可视元素"）
2. **拒绝使用用户真实数据目录**。带 `--data-dir` 指向真实目录时直接退出码 2 并报错，默认强制走临时目录

```
# 只跑验收测试并查看证据
tests\bin\AcceptanceTest.exe --report report.json

# 验证安全闸（应退出码 2 并拒绝）
tests\bin\AcceptanceTest.exe --data-dir <真实程序目录>
```

**最近一次运行结果：13 个套件、312 个断言全部通过（2026-10-21，v1.0.1）**

> 断言数变化：151 → 312。其中 `CatalogTest` 43→76（分类规则扩充）、新增 `CategoryOverrideTest` 30、
> 新增 `AcceptanceTest` 12、新增 `ActiveRowTest` 14、`CrashGuardTest` 19→45、`SettingsTest` 18→24、
> `OpenFolderTest` 新增 20、`DesktopRestoreTest` 新增 21、`FullscreenTest` 4→5、
> `VirtualPanelTest` 19→17（合并同类项）。原有断言**全部保留且继续通过**。

### 手动用例清单（实测结果）

| # | 用例 | 方法 | 结果 |
|---|---|---|---|
| 1 | `--restore-system` 还原 | 手动隐藏任务栏 + 伪造 `system-state.json` → 运行该命令 | ✅ 任务栏恢复、标记清除 |
| 2 | 看门狗兜底强杀 | 启动 → 隐藏任务栏 → `Stop-Process -Force` 主进程 | ✅ 看门狗检测到残留并还原 |
| 3 | 崩溃循环进安全模式 | 伪造 2 分钟内 3 次崩溃 → 启动 | ✅ 日志 WARN、桌面清理被跳过、任务栏强制显示 |
| 4 | `--safe-mode` 强制 | 清空计数后带参数启动 | ✅ 同上 |
| 5 | UIAutomation 不阻塞界面 | 观察 16 秒运行日志 | ✅ 无「后台调用超时」，定位正常 |
| 6 | 资源自检 | 启动后观察日志 | ✅ 记录内存 101 MB / 句柄 1524 个 |
| 7 | **全屏检测** | `FullscreenTest` 真建无边框全屏窗口 | ✅ 全屏时 true、关闭后 false |
| 8 | **TaskbarCreated** | `TaskbarCreatedTest` 真发消息 | ✅ 消息号唯一、子进程收到 |
| 9 | **监听缓冲区溢出** | `WatcherTest` 4KB 缓冲 + 8 线程并发写 4 万文件 | ⚠️ **未能复现**，详见下方说明 |
| 10 | **一键回滚全量** | 构造 6 项脏状态 → `--reset-all` | ✅ **6/6 全部清干净**，退出码 0 |
| 11 | **一键回滚保留配置** | 同上 → `--reset-all --keep-config` | ✅ 系统状态清干净，配置保留 |
| 12 | **运行标记写入** | 启动后读 `crash-guard.json` | ✅ 含 `running:true` / `runStarted` / `crashRecordedThisRun` |
| 13 | **心跳文件** | 启动后读 `config\heartbeat` | ✅ 存在且每 2 秒更新 |

**关于第 9 项，需要诚实说明**：实测在 Windows 10/11 上，即使把 `InternalBufferSize` 压到 4 KB、用 8 个线程并发写 4 万个文件，内核依然能把事件合并送达（产生约 8 万个事件、**0 次 Error**），无法稳定复现 `InternalBufferOverflowException`。

所以测试改为分两种情况：
- **真溢出了** → 验证兜底路径被执行
- **没溢出**（当前实际情况）→ 用反射验证 `Error` 处理器确实挂在事件上，并打印实测数据

结论：**Error 兜底路径的注册与接线是正确的，但触发条件依赖极端负载，无法在正常环境稳定复现**。这一项是「代码路径已验证、触发条件未复现」，不是「完全验证通过」。

### 需要手动验证的部分（无法自动测）

以下四项依赖真实交互或真实强杀，自动化测不了，**需要人工按步骤验证**：

| # | 项目 | 验证步骤 | 预期 |
|---|---|---|---|
| A | **看门狗卡死检测** | 1. 勾选「隐藏系统任务栏」<br>2. 用调试器或 `Debugger.Break` 让主进程界面线程卡住 15 秒以上<br>3. 观察任务栏 | 10 秒后任务栏自动恢复；`error.log` 出现「主进程连续 10 秒无响应」；主进程**不被结束** |
| B | **卡死后恢复重隐藏** | 承接 A，解除卡死后观察 | 任务栏可以再次隐藏，但同一次运行最多自动重隐藏 1 次 |
| C | **真实强杀不算/算异常退出** | 1. 正常启动并稳定 6 分钟（清计数）<br>2. 任务管理器强杀主进程<br>3. 重新启动，看 `crash-guard.json` | 强杀后 `running` 残留 → 重启时补记 1 次异常退出 |
| D | **首次勾选确认框** | 右键四叶草 → 勾选「自动移走桌面快捷方式」 | 弹说明框；点取消则不勾选、下次还会弹；点确定后 `desktopAutoCleanNoticeShown` 变 `true`，以后不再弹 |

## 自定义

- **改分类规则**：编辑 `src\core\Catalog.cs` 的 `GuessCategory`（按名称关键字）和 `src\core\NativeScanner.cs` 的 `GuessCategory`（按开始菜单目录），重新编译后右键四叶草 → 重新扫描应用。
- **手动加软件**：编辑 `config\manual-apps.json`，格式 `{ "apps":[{"name":"名称","path":"C:\\...\\app.exe","category":"其他"}] }`。
- **换图标**：直接替换 `res\clover.ico`（编译用）和 `res\clover.png`（界面用），再运行 `build.ps1`。
- **重新编译**：`powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1`（自动生成 `src\core\Version.cs`，版本号取自根目录 `VERSION`，另写入构建时间）
- **重新生成拼音表**：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-pinyin.ps1`（数据源是 pinyin-pro，MIT 许可，约 2 万字；详见 `tools\pinyin-data\README.md`）
- **改版本号**：编辑根目录 `VERSION`（形如 `1.0.0`），再重新编译。程序内版本、exe 文件属性、发布包名、git tag 都从它取，保证一致。
- **打包发布**：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-release.ps1`（生成 `dist\CloverStart-v<版本>.zip` 和 `.sha256` 校验文件）
- **改开关默认值**：编辑 `src\core\Settings.cs` 的字段初始值。
- **取消开机自启**：删掉 `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\四叶草启动器.lnk`，或右键四叶草把“开机自启”取消勾选。
- **恢复桌面快捷方式**：把 `desktop-backup\` 里的 `.lnk` 移回桌面即可（`manual-apps.json` 里对应条目可留可删）。

## 故障排查

- **任务栏不见了** → 见「紧急恢复」：任务管理器 →「文件」→「运行新任务」→ `explorer.exe`，或运行 `CloverStart.exe --restore-system`。
- **面板打不开 / 反复崩溃** → 运行 `CloverStart.exe --safe-mode`，它会关掉所有会动系统状态的功能，只留面板和按钮。
- **新软件没出现在面板里**：确认它确实在开始菜单里有快捷方式，或右键四叶草 → 重新扫描应用。
- **图标不对/是旧的**：删掉 `config\iconcache\` 目录，重启程序会自动重建。
- **按钮位置/大小不对**：看 `config\error.log` 里有没有「开始按钮定位失败」，确认任务栏在主屏；改 `src\ui\TriggerWindow.cs` 的 `Width/Height`（44×48）与 `Reposition()` 里的 `right + 4` 间距后重新编译。
- **想完全停用某个自动行为**：右键四叶草 → 取消对应勾选项，或直接改 `config\settings.json` 里的开关。
- **看门狗残留**：正常退出时会自动结束。若看到多余的 `CloverStart.exe --guard` 进程，直接结束即可，它不持有任何资源。


## 相关文档

| 文档 | 内容 |
|---|---|
| [项目说明.md](项目说明.md) | 实现细节与设计取舍：源码分层、系统状态设计、看门狗原理、已知限制 |
| [CHANGELOG.md](CHANGELOG.md) | 版本变更记录 |

## 许可证

[MIT](LICENSE) © 2026 clover-start contributors
