# 创建开机自启快捷方式并启动四叶草启动面板
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'app\CloverStart.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，请先运行 build.ps1" }

$startup = [Environment]::GetFolderPath('Startup')
$lnk = Join-Path $startup '四叶草启动器.lnk'
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = (Split-Path $exe)
$sc.IconLocation = "$exe,0"
$sc.Description = '四叶草启动面板'
$sc.Save()

Get-Process CloverStart -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Start-Process $exe

"已创建开机自启: $lnk"
"已启动: $exe"
