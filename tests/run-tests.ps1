# 编译并运行全部测试
#   单元测试（不依赖 WPF）：
#     CatalogTest     分类规则 / 隐藏分类 / ContainsName / JsonIO 往返与每日备份
#     PinyinTest      拼音表 / 全拼 / 首字母 / 边界 / 性能
#     SettingsTest    设置默认值 / 往返 / 未知字段保留 / 损坏回退 / 开关联动
#     CrashGuardTest  崩溃计数 / 时间窗口 / 安全模式判定 / 稳定清除
#     WatcherTest     文件监听溢出兜底 / Dispose 后无泄漏回调
#   集成测试（需要真实桌面会话）：
#     FullscreenTest  真建无边框全屏窗口，验证全屏检测与恢复
#     TaskbarCreatedTest  验证 TaskbarCreated 消息注册与接收
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $root 'src'
# 源码分两层：core 零 WPF 依赖，ui 依赖 WPF。
# 单元测试只引用 core；集成/验收测试引用 core + ui。
$coreDir = Join-Path $srcDir 'core'
$uiSrcDir = Join-Path $srcDir 'ui'
$testDir = $PSScriptRoot
$outDir = Join-Path $testDir 'bin'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe 不存在: $csc" }

$refDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$baseRefs = @(
    ('/r:' + (Join-Path $refDir 'System.dll')),
    ('/r:' + (Join-Path $refDir 'System.Core.dll'))
)
$webRef = ('/r:' + (Join-Path $refDir 'System.Web.Extensions.dll'))

# 集成测试要链接 src 下完整代码，需要 WPF + UIAutomation
$wpfRefs = $baseRefs + @(
    # System.Windows.Forms：FilesTabView 用 FolderBrowserDialog 选目录
    # （.NET Framework 自带，不是第三方库）
    ('/r:' + (Join-Path $refDir 'System.Windows.Forms.dll')),
    ('/r:' + (Join-Path $refDir 'System.Xaml.dll')),
    $webRef
)
foreach ($n in @('WindowsBase', 'PresentationCore', 'PresentationFramework', 'UIAutomationClient', 'UIAutomationTypes')) {
    $asm = [System.Reflection.Assembly]::LoadWithPartialName($n)
    if ($asm) { $wpfRefs += ('/r:' + $asm.Location) }
}

$script:failed = 0

function Invoke-Suite($name, $refs, $sources) {
    Write-Host "===== $name =====" -ForegroundColor Cyan
    $exe = Join-Path $outDir ($name + '.exe')
    & $csc /nologo /target:exe /codepage:65001 ('/out:' + $exe) $refs $sources 2>&1 |
        Where-Object { $_ -notmatch 'warning CS' }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "编译失败: $name" -ForegroundColor Red
        $script:failed++
        return
    }
    & $exe
    if ($LASTEXITCODE -ne 0) { $script:failed++ }
    Write-Host ''
}

# ---------------- 单元测试 ----------------
# Catalog 依赖这些（覆盖表 / 规则表 / 归一化 / 隐藏清单 / 存储），分类相关套件都要带上
$catalogDeps = @(
    (Join-Path $coreDir 'Catalog.cs'),
    (Join-Path $coreDir 'Migration.cs'),
    (Join-Path $coreDir 'Settings.cs'),
    (Join-Path $coreDir 'AppearancePresets.cs'),
    (Join-Path $coreDir 'AppPaths.cs'),
    (Join-Path $coreDir 'AppDedup.cs'),
    (Join-Path $coreDir 'ShortcutResolver.cs'),
    (Join-Path $coreDir 'RegistryScanner.cs'),
    (Join-Path $coreDir 'StoreAppScanner.cs'),
    (Join-Path $coreDir 'PortableScanner.cs'),
    (Join-Path $coreDir 'NativeScanner.cs'),
    (Join-Path $coreDir 'Log.cs'),
    (Join-Path $coreDir 'Pinyin.cs'),
    (Join-Path $coreDir 'PinyinData.cs'),
    (Join-Path $coreDir 'CategoryOverrides.cs'),
    (Join-Path $coreDir 'CategoryRules.cs'),
    (Join-Path $coreDir 'HiddenApps.cs'), (Join-Path $coreDir 'HideReason.cs'),
    (Join-Path $coreDir 'NameMatch.cs'),
    (Join-Path $coreDir 'JsonIO.cs'),
    (Join-Path $coreDir 'UsageStore.cs')
)

# 去重：需要扫描器 + 去重器
$dedupDeps = @(
    (Join-Path $coreDir 'Catalog.cs'),
    (Join-Path $coreDir 'Migration.cs'),
    (Join-Path $coreDir 'Settings.cs'),
    (Join-Path $coreDir 'AppearancePresets.cs'),
    (Join-Path $coreDir 'Log.cs'),
    (Join-Path $coreDir 'HiddenApps.cs'), (Join-Path $coreDir 'HideReason.cs'),
    (Join-Path $coreDir 'NameMatch.cs'),
    (Join-Path $coreDir 'JsonIO.cs'),
    (Join-Path $coreDir 'UsageStore.cs'),
    (Join-Path $coreDir 'Pinyin.cs'),
    (Join-Path $coreDir 'PinyinData.cs'),
    (Join-Path $coreDir 'AppPaths.cs'),
    (Join-Path $coreDir 'AppDedup.cs'),
    (Join-Path $coreDir 'ShortcutResolver.cs'),
    (Join-Path $coreDir 'RegistryScanner.cs'),
    (Join-Path $coreDir 'StoreAppScanner.cs'),
    (Join-Path $coreDir 'PortableScanner.cs'),
    (Join-Path $coreDir 'NativeScanner.cs'),
    (Join-Path $coreDir 'CategoryOverrides.cs'),
    (Join-Path $coreDir 'CategoryRules.cs')
)

# 去重：多来源合并、路径匹配、AUMID、同名不同程序、空值、确定性
Invoke-Suite 'DedupTest' ($baseRefs + $webRef) `
    ($dedupDeps + @((Join-Path $testDir 'DedupTest.cs')))

Invoke-Suite 'CatalogTest' ($baseRefs + $webRef) `
    ($catalogDeps + @((Join-Path $testDir 'CatalogTest.cs')))

# 分类覆盖表：优先级、重扫保持、损坏回退
Invoke-Suite 'CategoryOverrideTest' ($baseRefs + $webRef) `
    ($catalogDeps + @((Join-Path $testDir 'CategoryOverrideTest.cs')))

# 分类规则表：外置 TSV、匹配模式、归一化、热加载、坏文件回退
Invoke-Suite 'CategoryRulesTest' ($baseRefs + $webRef) `
    ($catalogDeps + @((Join-Path $testDir 'CategoryRulesTest.cs')))

# 隐藏应用：存储 / 目录过滤 / 与隐藏分类并存
Invoke-Suite 'HiddenAppsTest' ($baseRefs + $webRef) `
    ($catalogDeps + @((Join-Path $testDir 'HiddenAppsTest.cs')))

  # 一次性迁移：分类隐藏 → 逐条隐藏（幂等、不覆盖、读不出时保护原文件）
  Invoke-Suite 'MigrationTest' ($baseRefs + $webRef) `
      ($catalogDeps + @((Join-Path $testDir 'MigrationTest.cs')))

# 分类顺序与重命名：只改显示名、不动内部名

# 扫描链：便携条目每轮不能被 NativeScanner 抹掉（真实 bug 回归）
Invoke-Suite 'ScanChainTest' ($baseRefs + $webRef) `
    ($catalogDeps + @(
        (Join-Path $coreDir 'NativeScanner.cs'),
        (Join-Path $coreDir 'PortableScanner.cs'),
        (Join-Path $coreDir 'Native.cs'),
        (Join-Path $coreDir 'Log.cs'),
        (Join-Path $testDir 'ScanChainTest.cs')
    ))

# 运行状态判定：必须是精确路径匹配（弱匹配曾把 7 个 Python 应用误标为运行中）
Invoke-Suite 'RunningAppsTest' ($baseRefs + $webRef) `
    (@(
        (Join-Path $coreDir 'RunningApps.cs'),
          (Join-Path $coreDir 'AppPaths.cs'),
        (Join-Path $coreDir 'JsonIO.cs'),
        (Join-Path $testDir 'RunningAppsTest.cs')
    ))

Invoke-Suite 'PinyinTest' $baseRefs `
    @((Join-Path $coreDir 'Pinyin.cs'), (Join-Path $coreDir 'PinyinData.cs'), (Join-Path $testDir 'PinyinTest.cs'))

Invoke-Suite 'SettingsTest' ($baseRefs + $webRef) `
    @((Join-Path $coreDir 'Settings.cs'), (Join-Path $coreDir 'AppearancePresets.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'SettingsTest.cs'))


# 「文件」标签：自定义文件夹清单（路径校验 / 去重 / 上限 / 重命名 / 容错）
Invoke-Suite 'FileFoldersTest' $baseRefs `
    (@((Join-Path $coreDir 'FileFolders.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $testDir 'FileFoldersTest.cs')))

# 自定义标签清单：新建 / 重命名 / 删除 / 上限 / 内置名 / 持久化 / 容错
Invoke-Suite 'NavTabsTest' $baseRefs `
    (@((Join-Path $coreDir 'NavTabs.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'NavTabsTest.cs')))
Invoke-Suite 'WatcherTest' $baseRefs `
    ($baseRefs + $webRef + @((Join-Path $testDir 'WatcherTest.cs')))
# 「文件」标签：目录列举（过滤 / 排序 / 上限 / 只读）
Invoke-Suite 'FilesTabTest' $baseRefs `
    (@((Join-Path $coreDir 'FileScanner.cs'), (Join-Path $coreDir 'FileFolders.cs'), (Join-Path $coreDir 'FileExtGroups.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Pinyin.cs'), (Join-Path $coreDir 'PinyinData.cs'), (Join-Path $testDir 'FilesTabTest.cs')))
# 虚拟桌面存储：名称安全 / 坐标 / 桌面增删改名 / 持久化 / 容错
Invoke-Suite 'VirtualDesksTest' $baseRefs `
    (@((Join-Path $coreDir 'VirtualDesks.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'VirtualDesksTest.cs')))

# 自定义标签清单：新建 / 重命名 / 删除 / 上限 / 内置名 / 持久化 / 容错
Invoke-Suite 'NavTabsTest' $baseRefs `
    (@((Join-Path $coreDir 'NavTabs.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'NavTabsTest.cs')))


# 「全部」页：低价值条目识别规则
Invoke-Suite 'LowValueTest' $baseRefs `
    (@((Join-Path $coreDir 'LowValueRules.cs'), (Join-Path $testDir 'LowValueTest.cs')))


# 桌面保持无快捷方式：保留名单 / 规范化 / 自动失效
Invoke-Suite 'DesktopKeepTest' $baseRefs `
    (@((Join-Path $coreDir 'DesktopKeep.cs'), (Join-Path $coreDir 'DesktopCleaner.cs'), (Join-Path $coreDir 'Settings.cs'), (Join-Path $coreDir 'AppearancePresets.cs'), (Join-Path $coreDir 'FileOps.cs'), (Join-Path $coreDir 'Elevation.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'DesktopKeepTest.cs')))
# 「文件」标签：修改操作（重命名/删除/剪切粘贴/新建，走可注入的假文件系统）

# 隐藏项数据层 v2：原因 / 例外名单 / 向后兼容
Invoke-Suite 'HiddenAppsV2Test' $baseRefs `
    (@((Join-Path $coreDir 'HiddenApps.cs'), (Join-Path $coreDir 'HideReason.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'HiddenAppsV2Test.cs')))
Invoke-Suite 'FileOpsTest' $baseRefs `
    (@((Join-Path $coreDir 'FileOps.cs'), (Join-Path $coreDir 'Elevation.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $testDir 'FileOpsTest.cs')))
# 虚拟化面板的几何/滚动计算（用到 ItemsControl，需要 WPF 引用）

# ---------------- 集成测试 ----------------
# 链接 core + ui 下除 Program.cs 外的全部源码（Program.cs 有 Main，会冲突）
$allSrc = @(
    (Get-ChildItem $coreDir -Filter *.cs | ForEach-Object { $_.FullName })
) + @(
    (Get-ChildItem $uiSrcDir -Filter *.cs |
        Where-Object { $_.Name -ne 'Program.cs' } |
        ForEach-Object { $_.FullName })
)

# CrashGuardTest 要真正调用 CrashGuard（依赖 ErrorLog/Ui），所以走全量源码
Invoke-Suite 'CrashGuardTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'CrashGuardTest.cs')))

# 桌面还原：直接测 AppWatcher.RestoreLinks/MakeUniqueDest 的纯文件逻辑
# 打开文件所在位置：测 PanelWindow.TryBuildExplorerArgs 的路径解析
Invoke-Suite 'OpenFolderTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'OpenFolderTest.cs')))

# 毛玻璃背景：删壁纸页后 glassMode / accentFromWallpaper 仍工作
Invoke-Suite 'GlassBackdropTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'GlassBackdropTest.cs')))


# 外观参数：每个参数「真的改变渲染输出」的断言
Invoke-Suite 'AppearanceTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'AppearanceTest.cs')))


# 毛玻璃预设：参数映射、切换生效、custom 模式、保存重载、旧配置、边界
Invoke-Suite 'AppearancePresetTest' ($baseRefs + $webRef) `
    (@((Join-Path $coreDir 'AppearancePresets.cs'), (Join-Path $coreDir 'Settings.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'AppearancePresetTest.cs')))
  # 图标缓存：快捷方式去箭头、透明通道、缓存键版本化、商店应用

# 文字对比度保底：4 条约束（关闭清空 / 只补偿文字区 / 达标不改像素 / 实际合成验证）
Invoke-Suite 'ContrastTest' $baseRefs `
    (@((Join-Path $coreDir 'ContrastGuard.cs'), (Join-Path $coreDir 'Settings.cs'), (Join-Path $coreDir 'AppearancePresets.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'ContrastTest.cs')))
  Invoke-Suite 'IconTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'IconTest.cs')))
# 系统级搜索：Instant 行为回归 + 确认磁盘搜索逻辑已彻底移除
Invoke-Suite 'SearchTest' $baseRefs `
    (@((Join-Path $coreDir 'SystemSearch.cs'), (Join-Path $coreDir 'AppPaths.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'SearchTest.cs')))
Invoke-Suite 'FullscreenTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'FullscreenTest.cs')))

# 搜索合并：主（MatchCatalog）+ 补充（Instant）的去重/排序/上限
Invoke-Suite 'SearchMergeTest' $baseRefs `
    (@((Join-Path $coreDir 'SystemSearch.cs'), (Join-Path $coreDir 'AppPaths.cs'), (Join-Path $coreDir 'NameMatch.cs'), (Join-Path $coreDir 'Pinyin.cs'), (Join-Path $coreDir 'PinyinData.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $coreDir 'Catalog.cs'), (Join-Path $coreDir 'Settings.cs'), (Join-Path $coreDir 'AppearancePresets.cs'), (Join-Path $coreDir 'Migration.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'AppDedup.cs'), (Join-Path $coreDir 'ShortcutResolver.cs'), (Join-Path $coreDir 'RegistryScanner.cs'), (Join-Path $coreDir 'StoreAppScanner.cs'), (Join-Path $coreDir 'PortableScanner.cs'), (Join-Path $coreDir 'NativeScanner.cs'), (Join-Path $coreDir 'CategoryRules.cs'), (Join-Path $coreDir 'CategoryOverrides.cs'), (Join-Path $coreDir 'HiddenApps.cs'), (Join-Path $coreDir 'HideReason.cs'), (Join-Path $coreDir 'UsageStore.cs'), (Join-Path $testDir 'SearchMergeTest.cs')))

Invoke-Suite 'TaskbarCreatedTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'TaskbarCreatedTest.cs')))

# 任务栏行高亮判定：输入回调只读缓存后，「哪一行该亮」的纯逻辑
Invoke-Suite 'ActiveRowTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'ActiveRowTest.cs')))


# 右键菜单结构：顶层 8 项、顺序、分隔线、子菜单归属、状态绑定
Invoke-Suite 'MenuStructureTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'MenuStructureTest.cs')))

# 「整理与隐藏」外壳完整性：切标签不得覆盖宿主窗口的 Content
Invoke-Suite 'OrganizeShellTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'OrganizeShellTest.cs')))
# ---------------- 验收测试 ----------------

# 窗口位置记忆：存取 / 最大化 / 最小尺寸 / 越界 / 损坏回退
Invoke-Suite 'WindowPosTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'WindowPosTest.cs')))
# 真实创建 PanelWindow，检查界面确实画出来了，并输出 JSON 证据报告。

# 自定义背景图：导入 / 校验 / 缩放 / 回退 / 清除
Invoke-Suite 'BackgroundStoreTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'BackgroundStoreTest.cs')))
# 强制使用临时目录，拒绝接触用户真实 config（见 AcceptanceTest.cs 的安全闸）。

# Wallpaper Engine 集成：目录 / 解析 / 穿越防护 / 视频复制 / 暗度 / 回退
Invoke-Suite 'WallpaperEngineTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'WallpaperEngineTest.cs')))
$acceptanceReport = Join-Path $outDir 'acceptance-report.json'
Write-Host '===== AcceptanceTest =====' -ForegroundColor Cyan
$accExe = Join-Path $outDir 'AcceptanceTest.exe'
& $csc /nologo /target:exe /codepage:65001 ('/out:' + $accExe) $wpfRefs ($allSrc + @((Join-Path $testDir 'AcceptanceTest.cs'))) 2>&1 |
    Where-Object { $_ -notmatch 'warning CS' }
if ($LASTEXITCODE -ne 0) {
    Write-Host '编译失败: AcceptanceTest' -ForegroundColor Red
    $script:failed++
} else {
    & $accExe --report $acceptanceReport
    if ($LASTEXITCODE -ne 0) { $script:failed++ }
}
Write-Host ''

if ($script:failed -eq 0) {
    Write-Host '全部测试通过' -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($script:failed) 个套件失败" -ForegroundColor Red
    exit 1
}
