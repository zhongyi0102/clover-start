# 注册当前用户登录快速自启并启动四叶草启动面板
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

# 让程序自己的统一自启管理器完成注册与复核：
#   1. 优先创建“当前用户登录、零延迟、普通权限”的计划任务；
#   2. 任务注册成功并回读校验后，才删除旧启动文件夹快捷方式；
#   3. 若计划任务被系统策略拦截，自动保留兼容快捷方式，避免下次不启动。
$auto = Start-Process -FilePath $exe -ArgumentList '--enable-autostart' `
    -Wait -PassThru -WindowStyle Hidden
if ($auto.ExitCode -ne 0) {
    throw "设置开机自启失败（退出码 $($auto.ExitCode)），请查看 config\error.log"
}

Start-Process $exe

"已设置开机自启: 用户登录时立即启动（计划任务不可用时自动兼容）"
"已启动: $exe"
