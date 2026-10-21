# 编译 CloverStart.exe（.NET Framework 4.8 WPF，只用 csc.exe，无需 MSBuild）
#
# 分两层编译（借鉴分层项目，用编译器强制约束）：
#   src\core\  纯逻辑，零 WPF 依赖，可单独编译成 DLL
#   src\ui\    界面层，依赖 WPF，依赖 core
#
# 步骤：
#   1. 先用「只含非 WPF 程序集」的引用单独编译 core -> 失败即中断
#      （这是强制手段：core 里一旦引入 WPF 类型，编译就过不去）
#   2. 再把 core + ui 一起编译成单文件 exe（保持免安装特性）
#
# 为什么不在构建时产出两个 DLL：那样发布就要多带一个 Clover.Core.dll，
# 破坏「单文件 exe」这个核心特性。所以 core 的独立编译只用于验证，产物用完即删。
#
# 版本号来源：仓库根目录的 VERSION 文件（如 1.0.0）。
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$coreDir = Join-Path $srcDir 'core'
$uiDir = Join-Path $srcDir 'ui'
$outDir = Join-Path $root 'app'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

# ---- 版本号 + 构建时间（写进 src\core\Version.cs，编译进程序）----
# 版本号只有一个来源：仓库根目录的 VERSION 文件。
# 打 tag、发布包、程序内版本都从它取，避免三处各写各的对不上。
$versionFile = Join-Path $root 'VERSION'
if (-not (Test-Path $versionFile)) { throw "missing VERSION file (expected e.g. 1.0.0)" }
$appVersion = (Get-Content $versionFile -Raw -Encoding ascii).Trim()
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw "bad VERSION content: '$appVersion'" }

$stamp = Get-Date -Format 'yyyy.MM.dd.HHmm'
$buildTime = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$versionSource = Join-Path $coreDir 'Version.cs'
$versionCode = @"
// 由 build.ps1 自动生成，请勿手工编辑。
using System.Reflection;
[assembly: AssemblyVersion("$appVersion")]
[assembly: AssemblyFileVersion("$appVersion")]
[assembly: AssemblyProduct("四叶草启动面板")]
namespace Clover
{
    static class BuildInfo
    {
        public const string Version = "$appVersion";
        public const string Stamp = "$stamp";
        public const string BuildTime = "$buildTime";
        public static string Describe { get { return "四叶草 v" + Version + "（" + BuildTime + " 构建）"; } }
    }
}
"@
[System.IO.File]::WriteAllText($versionSource, $versionCode, (New-Object System.Text.UTF8Encoding($false)))

$refDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'

function Resolve-Refs($names) {
    $list = @()
    foreach ($n in $names) {
        $local = Join-Path $refDir ($n + '.dll')
        if (Test-Path $local) { $list += ('/r:' + $local); continue }
        $asm = [System.Reflection.Assembly]::LoadWithPartialName($n)
        if (-not $asm) { throw "assembly not found: $n" }
        $list += ('/r:' + $asm.Location)
    }
    return $list
}

# 非 WPF 引用：core 层只允许用这些
$coreRefNames = @('System', 'System.Core', 'System.Web.Extensions')
# 全量引用：ui 层需要 WPF
$allRefNames = @('System', 'System.Core', 'PresentationFramework', 'PresentationCore', 'WindowsBase',
    'System.Xaml', 'System.Web.Extensions', 'UIAutomationClient', 'UIAutomationTypes')

$coreRefs = Resolve-Refs $coreRefNames
$allRefs = Resolve-Refs $allRefNames

$coreSources = @(Get-ChildItem $coreDir -Filter *.cs | ForEach-Object { $_.FullName })
$uiSources = @(Get-ChildItem $uiDir -Filter *.cs | ForEach-Object { $_.FullName })
if ($coreSources.Count -eq 0) { throw "src\core has no .cs files" }
if ($uiSources.Count -eq 0) { throw "src\ui has no .cs files" }

# ---------------- 1. 分层校验：core 必须能脱离 WPF 单独编译 ----------------
$probeDll = Join-Path $env:TEMP ('Clover.Core.Probe.' + $PID + '.dll')
try {
    & $csc /nologo /target:library /codepage:65001 '/nowarn:0649' ('/out:' + $probeDll) $coreRefs $coreSources
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host '分层校验失败：src\core 出现了 WPF 依赖。' -ForegroundColor Red
        Write-Host ''
        Write-Host 'core 层必须保持零 WPF 依赖（这样它可以被独立测试、独立复用）。'
        Write-Host '如果有文件需要 WPF 类型，请把它移到 src\ui；'
        Write-Host '或者在 core 里用 Log 抽象代替 Ui.Log / ErrorLog。'
        Write-Host ''
        Write-Host '常见原因：'
        Write-Host '  - core 里写了 using System.Windows.*'
        Write-Host '  - core 里用了 Window / BitmapImage / DispatcherTimer 等 WPF 类型'
        Write-Host '  - core 里调了 Ui.Log / ErrorLog.*（应改成 Log.Info / Log.Warn / Log.Hook）'
        throw 'core layering check failed (see messages above)'
    }
} finally {
    if (Test-Path $probeDll) { Remove-Item $probeDll -Force -ErrorAction SilentlyContinue }
}

# ---------------- 2. 正式编译：core + ui 一起编成单文件 exe ----------------
$exe = Join-Path $outDir 'CloverStart.exe'
$ico = Join-Path $root 'res\clover.ico'

# 把界面图标嵌进程序集，这样单文件 exe 场景下没有外部 res\ 也能显示四叶草。
# 资源名固定为 CloverStart.clover.png（命名空间 + 文件名）。
$png = Join-Path $root 'res\clover.png'
$resourceArgs = @()
if (Test-Path $png) { $resourceArgs += ('/resource:' + $png + ',CloverStart.clover.png') }

$sources = $coreSources + $uiSources
$cscArgs = @('/nologo', '/target:winexe', '/codepage:65001', '/optimize+',
    ('/win32icon:' + $ico), ('/out:' + $exe)) + $resourceArgs + $allRefs + $sources
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }

$size = [math]::Round((Get-Item $exe).Length / 1KB)
"OK: $exe  ($size KB)  v$appVersion  ($stamp)  [core: $($coreSources.Count) files / ui: $($uiSources.Count) files]"
