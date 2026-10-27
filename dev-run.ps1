# 改完源码 → 一键编译并重启（实时预览用）
#
# 用途：改完 src 下的代码，双击桌面「四叶草-改完即跑」，
#       自动完成「停进程 → 编译 → 重新启动 → 弹出面板」，不用手动敲命令。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File dev-run.ps1
#   参数：
#     -Test       编译后先跑一遍全部测试（约 35 秒），通过才启动
#     -NoStart    只编译，不启动程序
#     -NoShow     启动后不自动弹出面板
#     -NoPause    结束后直接退出，不停留（自动化用）
#
# 注意：程序运行时会锁住 app\CloverStart.exe，编译器会报 CS0016，
#       所以本脚本第一步就是停掉正在运行的实例。

param(
    [switch]$Test,
    [switch]$NoStart,
    [switch]$NoShow,
    [switch]$NoPause
)

$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$exe = Join-Path $root 'app\CloverStart.exe'

function Step($text) {
    Write-Host ''
    Write-Host ("=== " + $text + " ===") -ForegroundColor Cyan
}

# 结束前停留，方便双击时看清结果；倒计时自动关闭，避免窗口一直挂着。
# -NoPause 或 stdin 被重定向（自动化场景）时直接跳过。
function Finish($code) {
    if ($NoPause) { exit $code }
    if ([Console]::IsInputRedirected) { exit $code }

    Write-Host ''
    Write-Host '这个窗口 10 秒后自动关闭（按回车立即关闭）...' -ForegroundColor DarkGray
    for ($i = 10; $i -gt 0; $i--) {
        Write-Host ("`r  倒计时 " + $i + " 秒 ") -NoNewline -ForegroundColor DarkGray
        if ([Console]::KeyAvailable) {
            $null = [Console]::ReadKey($true)
            break
        }
        Start-Sleep -Seconds 1
    }
    Write-Host ''
    exit $code
}

# ---------------- 1. 停掉正在运行的实例 ----------------
# 优先用 --exit 走优雅退出：它会清除运行标记，这次重启不被记成"异常退出"。
# 为什么重要：CrashGuard 的规则是「2 分钟内 3 次异常退出 → 进安全模式」。
# 以前这里直接 Stop-Process -Force，等于每次都记一次异常——改 3 次代码就误触安全模式。
Step '停止正在运行的实例'
$running = Get-Process CloverStart -ErrorAction SilentlyContinue
if ($running) {
    $running | ForEach-Object { Write-Host ("  停止 PID " + $_.Id) }

    # 第一步：请它自己干净退出
    $null = & $exe --exit 2>&1
    for ($i = 0; $i -lt 24; $i++) {
        Start-Sleep -Milliseconds 250
        if (-not (Get-Process CloverStart -ErrorAction SilentlyContinue)) { break }
    }

    # 第二步：还没退就说明它卡住了（或 --exit 没送达），这时才强杀
    $left = Get-Process CloverStart -ErrorAction SilentlyContinue
    if ($left) {
        Write-Host '  ⚠️ --exit 未生效（可能界面卡住），改用强制结束' -ForegroundColor Yellow
        $left | Stop-Process -Force -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Milliseconds 250
            if (-not (Get-Process CloverStart -ErrorAction SilentlyContinue)) { break }
        }
    }
}
else {
    Write-Host '  （没有在运行）'
}

# 确认 exe 不再被占用
if (Test-Path $exe) {
    for ($i = 0; $i -lt 10; $i++) {
        try {
            $fs = [System.IO.File]::Open($exe, 'Open', 'ReadWrite', 'None')
            $fs.Close()
            break
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }
}

# ---------------- 2. 编译 ----------------
Step '编译'
$buildScript = Join-Path $root 'build.ps1'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$buildOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript 2>&1
$buildCode = $LASTEXITCODE
$sw.Stop()

$buildOut | ForEach-Object { Write-Host ("  " + $_) }

if ($buildCode -ne 0) {
    Write-Host ''
    Write-Host ("编译失败（退出码 " + $buildCode + "）。已中止，程序不会重启。") -ForegroundColor Red
    Write-Host ''
    Write-Host '提示：'
    Write-Host '  · 上一条错误里的「文件名(行,列)」就是出问题的地方，按那个位置改'
    Write-Host '  · 若报 CS0016（写不出 app\CloverStart.exe），是有进程还占着文件，再跑一次即可'
    Write-Host '  · 若报「分层校验失败」，是 core 层引用了 WPF 类型，把该文件移到 src\ui'
    Finish 1
}

Write-Host ('  ✅ 编译成功，用时 ' + [math]::Round($sw.Elapsed.TotalSeconds, 1) + ' 秒') -ForegroundColor Green

# ---------------- 3. 可选：跑测试 ----------------
if ($Test) {
    Step '运行全部测试'
    $testScript = Join-Path $root 'tests\run-tests.ps1'
    $testOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $testScript 2>&1
    $testCode = $LASTEXITCODE
    $testOut | Select-String -Pattern '=====|ALL PASSED|FAILED|全部测试通过|个套件失败' |
        ForEach-Object { Write-Host ('  ' + $_.Line) }

    if ($testCode -ne 0) {
        Write-Host ''
        Write-Host '测试未通过。已中止，程序不会重启。' -ForegroundColor Red
        Finish 1
    }
}

if ($NoStart) {
    Write-Host ''
    Write-Host '（-NoStart）只编译不启动。' -ForegroundColor Yellow
    Write-Host ''
    Write-Host ('可执行文件：' + $exe)
    Finish 0
}

# ---------------- 4. 启动 ----------------
Step '启动'
Start-Process $exe
Start-Sleep -Seconds 6

$proc = Get-Process CloverStart -ErrorAction SilentlyContinue |
    Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if ($proc) {
    Write-Host ('  PID ' + $proc.Id + '  内存 ' + [math]::Round($proc.WorkingSet64 / 1MB) + ' MB')
}
else {
    Write-Host '  ⚠️ 进程没起来，看 config\error.log' -ForegroundColor Yellow
}

# ---------------- 5. 弹出面板（方便立刻看效果）----------------
if (-not $NoShow) {
    Start-Sleep -Seconds 2
    $null = & $exe --show 2>&1
    Write-Host '  已弹出面板'
}

Write-Host ''
Write-Host '完成。改完代码再跑一次这个脚本即可。' -ForegroundColor Green
Finish 0
