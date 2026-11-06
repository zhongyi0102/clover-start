# AO 语义路由服务 · 开机自启（用户级，无需管理员）
#
# 用 Windows 启动文件夹放一个 .vbs，登录时静默启动服务（无黑窗）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\semantic-router\install-autostart.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\semantic-router\install-autostart.ps1 -Uninstall
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\semantic-router\install-autostart.ps1 -Status

param(
    [switch]$Uninstall,
    [switch]$Status
)

$ErrorActionPreference = 'Stop'
$ServerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServerPy = Join-Path $ServerDir 'server.py'
$StartupDir = [Environment]::GetFolderPath('Startup')
$VbsPath = Join-Path $StartupDir 'AO-SemanticRouter.vbs'
$LogPath = Join-Path $env:TEMP 'ao-semantic-router.log'

# ---------- Status ----------
if ($Status) {
    if (Test-Path $VbsPath) {
        Write-Host "自启项已安装: $VbsPath" -ForegroundColor Green
        Write-Host "--- 内容 ---"
        Get-Content $VbsPath
        Write-Host "------------"
    } else {
        Write-Host "自启项未安装（$VbsPath 不存在）" -ForegroundColor Yellow
    }

    Write-Host ""
    try {
        $r = Invoke-RestMethod 'http://127.0.0.1:8765/health' -TimeoutSec 3
        Write-Host "服务状态: 在线 (模型 $($r.model), $($r.routes.Count) 条路由)" -ForegroundColor Green
    } catch {
        Write-Host "服务状态: 离线" -ForegroundColor Yellow
    }
    exit 0
}

# ---------- Uninstall ----------
if ($Uninstall) {
    if (Test-Path $VbsPath) {
        Remove-Item $VbsPath -Force
        Write-Host "已删除自启项: $VbsPath" -ForegroundColor Green
    } else {
        Write-Host "自启项不存在" -ForegroundColor Yellow
    }

    $conn = Get-NetTCPConnection -LocalPort 8765 -ErrorAction SilentlyContinue
    if ($conn) {
        $conn | ForEach-Object {
            $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
            if ($p -and $p.ProcessName -eq 'python') {
                Stop-Process -Id $p.Id -Force
                Write-Host "已停止服务 PID $($p.Id)" -ForegroundColor Green
            }
        }
    }
    exit 0
}

# ---------- Install ----------
Write-Host "=== 安装 AO 语义路由开机自启（用户级）===" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $ServerPy)) {
    throw "找不到 server.py: $ServerPy"
}

$uv = Get-Command uv -ErrorAction SilentlyContinue
if (-not $uv) {
    throw "找不到 uv。请先安装：https://docs.astral.sh/uv/"
}
Write-Host "uv       : $($uv.Source)"
Write-Host "服务目录 : $ServerDir"
Write-Host "启动目录 : $StartupDir"
Write-Host "日志     : $LogPath"
Write-Host ""

# 依赖检查
$venvPath = Join-Path $ServerDir '.venv'
if (-not (Test-Path $venvPath)) {
    Write-Host "首次安装需要下载依赖（含 torch，约 2GB）。" -ForegroundColor Yellow
    Write-Host "按 Enter 继续，Ctrl+C 取消"
    Read-Host | Out-Null
    Push-Location $ServerDir
    try {
        & uv sync
        if ($LASTEXITCODE -ne 0) { throw "uv sync 失败" }
    } finally {
        Pop-Location
    }
}

# 生成 VBS（静默启动，无控制台窗口）
# 关键：cmd /c 后面整个命令要再包一层双引号，且内层 uv.exe 路径也要引号。
# 实测写法：cmd /c ""C:\path\uv.exe" run server.py >> "log" 2>&1"
$uvPath = $uv.Source
$vbs = @"
' AO 语义路由服务 · 开机自启（由 install-autostart.ps1 生成）
' 静默启动 tools/semantic-router/server.py，日志追加到 %TEMP%\ao-semantic-router.log
Dim sh, logPath
Set sh = CreateObject("WScript.Shell")
logPath = sh.ExpandEnvironmentStrings("%TEMP%") & "\ao-semantic-router.log"
sh.CurrentDirectory = "$ServerDir"
sh.Run "cmd /c """"$uvPath"" run server.py >> """ & logPath & """ 2>&1""", 0, False
"@

$vbsEnc = New-Object System.Text.UnicodeEncoding($false, $true)
[System.IO.File]::WriteAllText($VbsPath, $vbs, $vbsEnc)
Write-Host "OK 已写入自启项: $VbsPath" -ForegroundColor Green

# 立即启动一次验证（先停旧的）
$conn = Get-NetTCPConnection -LocalPort 8765 -ErrorAction SilentlyContinue
if ($conn) {
    $conn | ForEach-Object {
        $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -eq 'python') { Stop-Process -Id $p.Id -Force }
    }
    Start-Sleep -Seconds 2
}

Write-Host ""
Write-Host "立即启动并等待就绪..."
& wscript.exe $VbsPath

$deadline = (Get-Date).AddSeconds(90)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    try {
        $r = Invoke-RestMethod 'http://127.0.0.1:8765/health' -TimeoutSec 2
        Write-Host "OK 服务就绪（模型 $($r.model)，$($r.routes.Count) 条路由）" -ForegroundColor Green
        $ready = $true
        break
    } catch { }
}

if (-not $ready) {
    Write-Host "WARN 90 秒内未就绪，检查日志: $LogPath" -ForegroundColor Yellow
    if (Test-Path $LogPath) {
        Write-Host "--- 日志尾部 ---"
        Get-Content $LogPath -Tail 20
    }
}

Write-Host ""
Write-Host "后续操作："
Write-Host "  查看状态: powershell -File tools\semantic-router\install-autostart.ps1 -Status"
Write-Host "  卸载:     powershell -File tools\semantic-router\install-autostart.ps1 -Uninstall"
Write-Host ""
