# 创建开机自启快捷方式并启动四叶草启动面板
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'app\CloverStart.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，请先运行 build.ps1" }

# 更新安装时先请旧实例走正常退出路径：还原系统状态并清除运行标记，
# 避免把一次正常升级误记成崩溃。只有 6 秒内仍未退出才强制结束。
$running = Get-Process CloverStart -ErrorAction SilentlyContinue
if ($running) {
    $null = & $exe --exit 2>&1
    for ($i = 0; $i -lt 24; $i++) {
        Start-Sleep -Milliseconds 250
        if (-not (Get-Process CloverStart -ErrorAction SilentlyContinue)) { break }
    }

    $left = Get-Process CloverStart -ErrorAction SilentlyContinue
    if ($left) {
        Write-Warning '--exit 超时，正在强制结束无响应的旧实例。'
        $left | Stop-Process -Force -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Milliseconds 250
            if (-not (Get-Process CloverStart -ErrorAction SilentlyContinue)) { break }
        }
    }
}

$startup = [Environment]::GetFolderPath('Startup')
$lnk = Join-Path $startup '四叶草启动器.lnk'
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = (Split-Path $exe)
$sc.IconLocation = "$exe,0"
$sc.Description = '四叶草启动面板'
$sc.Save()

Start-Process $exe

"已创建开机自启: $lnk"
"已启动: $exe"
