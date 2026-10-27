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
    (Join-Path $coreDir 'CategoryOverrides.cs'),
    (Join-Path $coreDir 'CategoryRules.cs'),
    (Join-Path $coreDir 'HiddenApps.cs'),
    (Join-Path $coreDir 'NameMatch.cs'),
    (Join-Path $coreDir 'JsonIO.cs'),
    (Join-Path $coreDir 'UsageStore.cs')
)

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

Invoke-Suite 'PinyinTest' $baseRefs `
    @((Join-Path $coreDir 'Pinyin.cs'), (Join-Path $coreDir 'PinyinData.cs'), (Join-Path $testDir 'PinyinTest.cs'))

Invoke-Suite 'SettingsTest' ($baseRefs + $webRef) `
    @((Join-Path $coreDir 'Settings.cs'), (Join-Path $coreDir 'JsonIO.cs'), (Join-Path $testDir 'SettingsTest.cs'))

Invoke-Suite 'WatcherTest' $baseRefs `
    @((Join-Path $testDir 'WatcherTest.cs'))

# 虚拟化面板的几何/滚动计算（用到 ItemsControl，需要 WPF 引用）
Invoke-Suite 'VirtualPanelTest' $wpfRefs `
    @((Join-Path $testDir 'VirtualPanelTest.cs'))

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
Invoke-Suite 'DesktopRestoreTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'DesktopRestoreTest.cs')))

# 打开文件所在位置：测 PanelWindow.TryBuildExplorerArgs 的路径解析
Invoke-Suite 'OpenFolderTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'OpenFolderTest.cs')))

Invoke-Suite 'FullscreenTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'FullscreenTest.cs')))

Invoke-Suite 'TaskbarCreatedTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'TaskbarCreatedTest.cs')))

# 任务栏行高亮判定：输入回调只读缓存后，「哪一行该亮」的纯逻辑
Invoke-Suite 'ActiveRowTest' $wpfRefs `
    ($allSrc + @((Join-Path $testDir 'ActiveRowTest.cs')))

# ---------------- 验收测试 ----------------
# 真实创建 PanelWindow，检查界面确实画出来了，并输出 JSON 证据报告。
# 强制使用临时目录，拒绝接触用户真实 config（见 AcceptanceTest.cs 的安全闸）。
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
