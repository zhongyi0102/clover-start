# 打包发布：生成可分发的 zip（解压即用，免安装）
#
# 产物结构（与程序运行时的目录约定一致）：
#   CloverStart-v1.0.0\
#     app\CloverStart.exe    主程序
#     res\clover.png         界面图标（运行时必需）
#     res\clover.ico         备用图标
#     启动.bat               双击启动
#     安装开机自启.bat       创建开机自启
#     使用说明.txt
#
# 注意：exe 必须放在 app\ 子目录里——程序用 `exe目录\..` 推算程序根目录，
#       直接放在根目录会导致配置和 res 都找不到。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-release.ps1
#      可加 -SkipBuild 跳过重新编译
param(
    [switch]$SkipBuild,
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $root 'dist'
$pkgName = "CloverStart-v$Version"
$pkgDir = Join-Path $distDir $pkgName
$zipPath = Join-Path $distDir ($pkgName + '.zip')

# ---------------- 1. 编译 ----------------
if (-not $SkipBuild) {
    Write-Host '编译中...' -ForegroundColor Cyan
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }
}

$exe = Join-Path $root 'app\CloverStart.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，请先运行 build.ps1 或去掉 -SkipBuild" }

# ---------------- 2. 准备目录 ----------------
if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $pkgDir 'app') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $pkgDir 'res') | Out-Null

# ---------------- 3. 复制必需文件 ----------------
Copy-Item $exe (Join-Path $pkgDir 'app\CloverStart.exe') -Force
Copy-Item (Join-Path $root 'res\clover.png') (Join-Path $pkgDir 'res\clover.png') -Force
Copy-Item (Join-Path $root 'res\clover.ico') (Join-Path $pkgDir 'res\clover.ico') -Force
Copy-Item (Join-Path $root 'LICENSE') $pkgDir -Force
Copy-Item (Join-Path $root 'README.md') (Join-Path $pkgDir 'README.md') -Force

# ---------------- 4. 生成启动脚本 ----------------
# 用 GBK 编码写 .bat，避免中文在 cmd 里乱码
$batLaunch = @'
@echo off
cd /d "%~dp0"
start "" "app\CloverStart.exe"
'@
[System.IO.File]::WriteAllText((Join-Path $pkgDir '启动.bat'), $batLaunch,
    [System.Text.Encoding]::GetEncoding(936))

$batInstall = @'
@echo off
cd /d "%~dp0"
echo 正在创建开机自启...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$exe = Join-Path (Get-Location) 'app\CloverStart.exe'; $startup = [Environment]::GetFolderPath('Startup'); $lnk = Join-Path $startup '四叶草启动器.lnk'; $ws = New-Object -ComObject WScript.Shell; $sc = $ws.CreateShortcut($lnk); $sc.TargetPath = $exe; $sc.WorkingDirectory = (Split-Path $exe); $sc.IconLocation = \"$exe,0\"; $sc.Description = '四叶草启动面板'; $sc.Save(); Start-Process $exe"
echo.
echo 已创建开机自启并启动。
pause
'@
[System.IO.File]::WriteAllText((Join-Path $pkgDir '安装开机自启.bat'), $batInstall,
    [System.Text.Encoding]::GetEncoding(936))

# ---------------- 5. 使用说明 ----------------
$readme = @'
四叶草启动面板 v1.0.0 —— 使用说明

【怎么运行】
  双击「启动.bat」
  或者直接双击 app\CloverStart.exe

【需要什么】
  Windows 10 / 11
  .NET Framework 4.8（Win10 1903 以后系统自带，一般不用装）

【开机自启】
  双击「安装开机自启.bat」。
  取消：右键四叶草按钮 → 取消勾选「开机自启」，
        或删除 %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\四叶草启动器.lnk

【主要操作】
  · 点击任务栏里的四叶草按钮，或按 Ctrl + Alt + 空格 打开/收起面板
  · 面板顶部搜索框支持中文、英文、拼音（全拼 weixin / 首字母 wx）
  · 方向键选中、回车启动、Esc 关闭
  · 右键应用磁贴 → 打开文件所在位置
  · 右键四叶草按钮 → 各种开关和设置

【出问题了怎么办】
  任务栏不见了：
    任务管理器(Ctrl+Shift+Esc) → 文件 → 运行新任务 → 输入 explorer.exe → 确定
    或：在本目录打开命令行，运行 app\CloverStart.exe --restore-system

  面板打不开 / 反复崩溃：
    运行 app\CloverStart.exe --safe-mode

  想彻底清除：
    app\CloverStart.exe --reset-all
    （会还原所有系统改动，并清理配置和缓存）

【数据在哪】
  首次运行会在本目录生成 config\ 文件夹，里面是应用清单、设置、使用记录。
  desktop-backup\ 存放被移走的桌面快捷方式（默认不会自动移走）。

【卸载】
  1. 运行 app\CloverStart.exe --reset-all
  2. 删除整个文件夹
  3. 如果开了开机自启，先删掉上面提到的那个 .lnk

完整文档见 README.md
'@
[System.IO.File]::WriteAllText((Join-Path $pkgDir '使用说明.txt'), $readme,
    (New-Object System.Text.UTF8Encoding($true)))

# ---------------- 6. 打包 zip ----------------
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $pkgDir -DestinationPath $zipPath -CompressionLevel Optimal
$zipSize = [math]::Round((Get-Item $zipPath).Length / 1KB, 1)

# ---------------- 7. 汇总 ----------------
Write-Host ''
Write-Host '打包完成' -ForegroundColor Green
Write-Host ('  目录: ' + $pkgDir)
Write-Host ('  压缩包: ' + $zipPath + '  (' + $zipSize + ' KB)')
Write-Host ''
Write-Host '内容:' -ForegroundColor Cyan
Get-ChildItem $pkgDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($pkgDir.Length + 1)
    Write-Host ('  {0,-30} {1,8} KB' -f $rel, [math]::Round($_.Length / 1KB, 1))
}

# 校验 zip 能正常解压
Write-Host ''
Write-Host '校验压缩包...' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $count = $zip.Entries.Count
    $hasExe = ($zip.Entries | Where-Object { $_.FullName -like '*CloverStart.exe' }).Count -gt 0
    $hasPng = ($zip.Entries | Where-Object { $_.FullName -like '*clover.png' }).Count -gt 0
    Write-Host ("  条目数: $count")
    Write-Host ("  含主程序: " + $(if ($hasExe) { '是' } else { '否' }))
    Write-Host ("  含界面图标: " + $(if ($hasPng) { '是' } else { '否' }))
    if (-not ($hasExe -and $hasPng)) { throw '压缩包缺少关键文件' }
} finally {
    $zip.Dispose()
}
Write-Host '  校验通过' -ForegroundColor Green
