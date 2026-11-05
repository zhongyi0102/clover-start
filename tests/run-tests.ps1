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
# CI / 无桌面会话：追加 -CoreOnly，只跑不链接 WPF 界面的套件。
param(
    [switch]$CoreOnly
)

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
$script:executed = 0
$script:startedAt = Get-Date
$script:results = @()
$script:desktopSuites = @(
    'CrashGuardTest', 'OpenFolderTest', 'GlassBackdropTest', 'AppearanceTest',
    'IconTest', 'FullscreenTest', 'TaskbarCreatedTest', 'ActiveRowTest',
    'MenuStructureTest', 'OrganizeShellTest', 'DeskRootDialogTest', 'DropHintTest',
    'WindowPosTest', 'BackgroundStoreTest', 'WallpaperEngineTest'
)

function Invoke-Suite($name, $refs, $sources) {
    if ($CoreOnly -and $script:desktopSuites -contains $name) {
        Write-Host "===== $name（CoreOnly 跳过）=====" -ForegroundColor DarkGray
        return
    }

    Write-Host "===== $name =====" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'passed'
    $script:executed++
    $exe = Join-Path $outDir ($name + '.exe')
    & $csc /nologo /target:exe /codepage:65001 ('/out:' + $exe) $refs $sources 2>&1 |
        Where-Object { $_ -notmatch 'warning CS' }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "编译失败: $name" -ForegroundColor Red
        $script:failed++
        $status = 'compile_failed'
        $sw.Stop()
        $script:results += [pscustomobject]@{
            name=$name; status=$status; assertions=0; durationMs=[int]$sw.ElapsedMilliseconds
        }
        return
    }
    $suiteOutput = @(& $exe 2>&1)
    $suiteExitCode = $LASTEXITCODE
    $suiteOutput | ForEach-Object { Write-Host $_ }
    $assertions = @($suiteOutput | Where-Object { ([string]$_) -match '^\s*PASS\s' }).Count
    if ($suiteExitCode -ne 0) { $script:failed++; $status = 'failed' }
    $sw.Stop()
    $script:results += [pscustomobject]@{
        name=$name; status=$status; assertions=$assertions; durationMs=[int]$sw.ElapsedMilliseconds
    }
    Write-Host ''
}

function Complete-TestRun {
    $summaryPath = Join-Path $outDir 'test-summary.json'
    [pscustomobject]@{
        generatedAt = (Get-Date).ToString('o')
        startedAt = $script:startedAt.ToString('o')
        mode = $(if ($CoreOnly) { 'core-only' } else { 'full' })
        suites = $script:executed
        failed = $script:failed
        passed = $script:executed - $script:failed
        assertions = [int](($script:results | Measure-Object -Property assertions -Sum).Sum)
        results = $script:results
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    if ($script:failed -eq 0) {
        Write-Host "全部测试通过（$script:executed 个套件）" -ForegroundColor Green
        Write-Host "机器可读汇总: $summaryPath" -ForegroundColor DarkGray
        exit 0
    }

    Write-Host "$($script:failed) 个套件失败" -ForegroundColor Red
    exit 1
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
    (@((Join-Path $coreDir 'FileFolders.cs'), (Join-Path $coreDir 'DropSpec.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $testDir 'FileFoldersTest.cs')))

# 自定义标签清单：新建 / 重命名 / 删除 / 上限 / 内置名 / 持久化 / 容错
Invoke-Suite 'NavTabsTest' $baseRefs `
    (@((Join-Path $coreDir 'NavTabs.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'NavTabsTest.cs')))
Invoke-Suite 'SafeCallTest' $baseRefs `
    (@((Join-Path $coreDir 'SafeCall.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'SafeCallTest.cs')))
Invoke-Suite 'ScanServiceTest' $baseRefs `
    (@((Join-Path $coreDir 'ScanService.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'ScanServiceTest.cs')))
Invoke-Suite 'WatcherTest' $baseRefs `
    @((Join-Path $testDir 'WatcherTest.cs'))
# 「文件」标签：目录列举（过滤 / 排序 / 上限 / 只读）
Invoke-Suite 'FilesTabTest' $baseRefs `
    (@((Join-Path $coreDir 'FileScanner.cs'), (Join-Path $coreDir 'FileFolders.cs'), (Join-Path $coreDir 'DropSpec.cs'), (Join-Path $coreDir 'FileExtGroups.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Pinyin.cs'), (Join-Path $coreDir 'PinyinData.cs'), (Join-Path $testDir 'FilesTabTest.cs')))
# 虚拟桌面存储：名称安全 / 坐标 / 桌面增删改名 / 持久化 / 容错
Invoke-Suite 'VirtualDesksTest' $baseRefs `
    (@((Join-Path $coreDir 'VirtualDesks.cs'), (Join-Path $coreDir 'VerifiedFileMove.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'VirtualDesksTest.cs')))
# 桌面存放目录切换：校验 / 盘点 / 全有或全无迁移 / 回滚 / 路径越界
Invoke-Suite 'DeskRootChangeTest' $baseRefs `
    (@((Join-Path $coreDir 'DeskRootChange.cs'), (Join-Path $coreDir 'VirtualDesks.cs'), (Join-Path $coreDir 'VerifiedFileMove.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'DeskRootChangeTest.cs')))
# 删除虚拟桌面：放回桌面 / 保留 / 同名冲突 / 占用失败 / 空桌面
Invoke-Suite 'DeskRemovalTest' $baseRefs `
    (@((Join-Path $coreDir 'DeskRemoval.cs'), (Join-Path $coreDir 'VerifiedFileMove.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'DeskRemovalTest.cs')))
# 拖入导入：过滤 exe/lnk / 移动 vs 复制 / 冲突改名 / 文件夹 / 占用 / 来源判定
# （DeskImporter 现在引用 DesktopKeep.DesktopDirs 判来源，所以要带它及其依赖）
Invoke-Suite 'DeskImporterTest' $baseRefs `
    (@((Join-Path $coreDir 'DeskImporter.cs'), (Join-Path $coreDir 'DropSpec.cs'), (Join-Path $coreDir 'VerifiedFileMove.cs'), (Join-Path $coreDir 'DesktopKeep.cs'), (Join-Path $coreDir 'DesktopCleaner.cs'), (Join-Path $coreDir 'Settings.cs'), (Join-Path $coreDir 'AppearancePresets.cs'), (Join-Path $coreDir 'FileOps.cs'), (Join-Path $coreDir 'Elevation.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'DeskImporterTest.cs')))
# 放置规则：正常 / 全拒 / 混合 / 超数量 / 超大小 / 空输入 / 目录模式 / 按原因汇总
Invoke-Suite 'DropSpecTest' $baseRefs `
    (@((Join-Path $coreDir 'DropSpec.cs'), (Join-Path $testDir 'DropSpecTest.cs')))
# 拒绝反馈文案：单原因 / 多原因排序 / 全拒 / 无被拒返回 null / 扩展名超 3 种 / 边界
Invoke-Suite 'DropSummaryTest' $baseRefs `
    (@((Join-Path $coreDir 'DropSpec.cs'), (Join-Path $testDir 'DropSummaryTest.cs')))
# 用户文件安全移动：临时副本核对 / 原子发布 / 失败保留源
Invoke-Suite 'VerifiedFileMoveTest' $baseRefs `
    (@((Join-Path $coreDir 'VerifiedFileMove.cs'), (Join-Path $testDir 'VerifiedFileMoveTest.cs')))
# 收纳桌面 / 放回桌面
Invoke-Suite 'DeskCollectorTest' $baseRefs `
    (@((Join-Path $coreDir 'DeskCollector.cs'), (Join-Path $coreDir 'VerifiedFileMove.cs'), (Join-Path $coreDir 'Log.cs'), (Join-Path $testDir 'DeskCollectorTest.cs')))
# 旧桌面迁移冲突提示：同一冲突只弹一次，重启后仍记住
Invoke-Suite 'DeskMigrationNoticeTest' $baseRefs `
    (@((Join-Path $coreDir 'DeskMigrationNotice.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $testDir 'DeskMigrationNoticeTest.cs')))
# 拖出落点判定：类名 → 桌面/资源管理器/其他 → 是否移动源
Invoke-Suite 'DropTargetTest' $baseRefs `
    (@((Join-Path $coreDir 'DropTarget.cs'), (Join-Path $coreDir 'Native.cs'), (Join-Path $testDir 'DropTargetTest.cs')))
# 虚拟桌面拖动手势：画布内自由摆放 / 边缘无缝切系统拖放
Invoke-Suite 'DesktopDragGestureTest' $baseRefs `
    (@((Join-Path $coreDir 'DesktopDragGesture.cs'), (Join-Path $testDir 'DesktopDragGestureTest.cs')))

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

# 「常用」名单：右键「添加到常用」的人工固定 + 无上限 + 隐藏项过滤。
# 需要链接 ui 源码：直接调用生产函数 PanelWindow.IsCommonByUsage()。
Invoke-Suite 'CommonPinTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'CommonPinTest.cs')))

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

# 更改存放目录对话框：默认项必须是「搬过去」+ 文案必须写明会搬多少个文件
Invoke-Suite 'DeskRootDialogTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'DeskRootDialogTest.cs')))

# 放置提示层：常驻不增删子元素 / 只切 Visibility / 不接收命中 / 令牌 / 高亮态 / 文案
Invoke-Suite 'DropHintTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'DropHintTest.cs')))

# 文件夹来源拖入（接入点 3）：文案常量 / 混合拖入 / 名额不足 / 已满 / 字段行为
Invoke-Suite 'FolderSourceDropTest' $baseRefs `
    (@((Join-Path $coreDir 'DropSpec.cs'), (Join-Path $testDir 'FolderSourceDropTest.cs')))
# 拖放光标判定：内部拖动不变 / 不接收页 None / 页判定与 OnPanelDrop 分支一致 / 8 种矩阵
Invoke-Suite 'DropCursorTest' $baseRefs `
    (@((Join-Path $coreDir 'DropCursor.cs'), (Join-Path $coreDir 'DesktopDragGesture.cs'), (Join-Path $testDir 'DropCursorTest.cs')))
# 桌面点击路径：双击打开（此前从未实现）/ 修饰键不误触 / 空白框选 / 24 种矩阵
Invoke-Suite 'DeskClickActionTest' $baseRefs `
    (@((Join-Path $coreDir 'DeskClickAction.cs'), (Join-Path $testDir 'DeskClickActionTest.cs')))
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

if ($CoreOnly) {
    Complete-TestRun
}

$acceptanceReport = Join-Path $outDir 'acceptance-report.json'
Write-Host '===== AcceptanceTest =====' -ForegroundColor Cyan
$accExe = Join-Path $outDir 'AcceptanceTest.exe'
$acceptanceSw = [System.Diagnostics.Stopwatch]::StartNew()
$acceptanceStatus = 'passed'
& $csc /nologo /target:exe /codepage:65001 ('/out:' + $accExe) $wpfRefs ($allSrc + @((Join-Path $testDir 'AcceptanceTest.cs'))) 2>&1 |
    Where-Object { $_ -notmatch 'warning CS' }
if ($LASTEXITCODE -ne 0) {
    Write-Host '编译失败: AcceptanceTest' -ForegroundColor Red
    $script:failed++
    $acceptanceStatus = 'compile_failed'
} else {
    $acceptanceOutput = @(& $accExe --report $acceptanceReport 2>&1)
    $acceptanceExitCode = $LASTEXITCODE
    $acceptanceOutput | ForEach-Object { Write-Host $_ }
    $acceptanceAssertions = @($acceptanceOutput |
        Where-Object { ([string]$_) -match '^\s*PASS\s' }).Count
    if ($acceptanceExitCode -ne 0) { $script:failed++; $acceptanceStatus = 'failed' }
}
$acceptanceSw.Stop()
Write-Host ''

$script:executed++
$script:results += [pscustomobject]@{
    name = 'AcceptanceTest'
    status = $acceptanceStatus
    assertions = $(if ($acceptanceStatus -eq 'compile_failed') { 0 } else { $acceptanceAssertions })
    durationMs = [int]$acceptanceSw.ElapsedMilliseconds
}

Complete-TestRun
