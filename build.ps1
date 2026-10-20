# 编译 CloverStart.exe（.NET Framework 4.8 WPF，只用 csc.exe，无需 MSBuild）
# 每次编译都会生成 src\Version.cs（版本号 + 构建时间），并打印出来。
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$outDir = Join-Path $root 'app'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe 不存在: $csc" }

# ---- 版本号 + 构建时间（写进 src\Version.cs，编译进程序）----
# 版本号只有一个来源：仓库根目录的 VERSION 文件（如 1.0.0）。
# 打 tag、发布包、程序内版本都从它取，避免三处各写各的对不上。
$versionFile = Join-Path $root 'VERSION'
if (-not (Test-Path $versionFile)) { throw "找不到 $versionFile（内容形如 1.0.0）" }
$appVersion = (Get-Content $versionFile -Raw -Encoding ascii).Trim()
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION 内容不合法: '$appVersion'（应形如 1.0.0）" }

$stamp = Get-Date -Format 'yyyy.MM.dd.HHmm'
$buildTime = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$versionFile = Join-Path $srcDir 'Version.cs'
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
[System.IO.File]::WriteAllText($versionFile, $versionCode, (New-Object System.Text.UTF8Encoding($false)))

$refNames = @('System', 'System.Core', 'PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Xaml', 'System.Web.Extensions', 'UIAutomationClient', 'UIAutomationTypes')
$refDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @()
foreach ($n in $refNames) {
    $local = Join-Path $refDir ($n + '.dll')
    if (Test-Path $local) { $refs += ('/r:' + $local); continue }
    $asm = [System.Reflection.Assembly]::LoadWithPartialName($n)
    if (-not $asm) { throw "找不到程序集: $n" }
    $refs += ('/r:' + $asm.Location)
}

$sources = @(Get-ChildItem $srcDir -Filter *.cs | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "src 目录没有 .cs 文件" }

$exe = Join-Path $outDir 'CloverStart.exe'
$ico = Join-Path $root 'res\clover.ico'

# 把界面图标嵌进程序集，这样单文件 exe 场景下没有外部 res\ 也能显示四叶草。
# 资源名固定为 CloverStart.clover.png（命名空间 + 文件名）。
$png = Join-Path $root 'res\clover.png'
$resourceArgs = @()
if (Test-Path $png) { $resourceArgs += ('/resource:' + $png + ',CloverStart.clover.png') }

$cscArgs = @('/nologo', '/target:winexe', '/codepage:65001', '/optimize+', ('/win32icon:' + $ico), ('/out:' + $exe)) + $resourceArgs + $refs + $sources
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }

$size = [math]::Round((Get-Item $exe).Length / 1KB)
"OK: $exe  ($size KB)  v$appVersion  ($stamp)"
