# 临时预设切换脚本（第 3 个提交做出界面后即可删除）
#
# 为什么需要它：第 2 个提交只做了预设的核心逻辑，外观窗口在
# 第 3 个提交才做。现在要试 6 个预设的观感，只能手改 settings.json，
# 而手改容易漏键、容易忘重启 —— 这个脚本把两件事一起做了。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\try-preset.ps1
#       （不带参数 = 列出所有预设和它们的参数，不改任何东西）
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\try-preset.ps1 classic
#       （切到经典毛玻璃并重启程序）
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\try-preset.ps1 -Cycle
#       （按顺序逐个切换，每切一次停 6 秒让你看，看完按 Ctrl+C 结束）

param(
    [Parameter(Position=0)]
    [string]$Preset,

    [switch]$Cycle,

    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $root 'config\settings.json'
$exe = Join-Path $root 'app\CloverStart.exe'

# ---- 预设表（与 src\core\AppearancePresets.cs 保持一致）----
# 这里重复一份是为了脚本不依赖编译产物；改预设时两处都要改。
$presets = @(
    @{ Key='custom';  Name='自定义';     Opacity=$null; Blur=$null; Tint=$null;      TintStrength=$null; Brightness=$null; Accent=$null;      Theme='keep'  },
    @{ Key='classic'; Name='经典毛玻璃'; Opacity=80;    Blur=10;   Tint='';         TintStrength=10;    Brightness=0;     Accent='';         Theme='keep'  },
    @{ Key='frosted'; Name='磨砂玻璃';   Opacity=78;    Blur=25;   Tint='#DCE4E8';  TintStrength=22;    Brightness=6;    Accent='';         Theme='keep'  },
    @{ Key='dark';    Name='深色玻璃';   Opacity=92;    Blur=12;   Tint='#172A30';  TintStrength=45;    Brightness=-8;    Accent='#4FC3D9';  Theme='dark'  },
    @{ Key='light';   Name='浅色玻璃';   Opacity=65;    Blur=8;    Tint='#FFFFFF';  TintStrength=30;    Brightness=18;    Accent='#388C48';  Theme='light' },
    @{ Key='liquid';  Name='液态玻璃';   Opacity=45;    Blur=3;    Tint='#7FA8B8';  TintStrength=26;    Brightness=-18;   Accent='';         Theme='keep'  }
)

function Show-Presets {
    Write-Host ''
    Write-Host '可用的毛玻璃预设：' -ForegroundColor Cyan
    Write-Host ''
    Write-Host ('  {0,-10} {1,-12} {2,8} {3,6} {4,10} {5,8} {6,9} {7,10} {8,6}' -f 'Key','名称','opacity','blur','tint','tintStr','bright','accent','theme')
    Write-Host ('  ' + ('-' * 88))
    foreach ($p in $presets) {
        if ($p.Key -eq 'custom') {
            Write-Host ('  {0,-10} {1,-12} {2,8} {3,6} {4,10} {5,8} {6,9} {7,10} {8,6}' -f `
                $p.Key, $p.Name, '(保持)', '-', '-', '-', '-', '-', 'keep') -ForegroundColor DarkGray
        } else {
            $tint = if ([string]::IsNullOrEmpty($p.Tint)) { '(强调色)' } else { $p.Tint }
            $acc = if ([string]::IsNullOrEmpty($p.Accent)) { '(默认)' } else { $p.Accent }
            Write-Host ('  {0,-10} {1,-12} {2,8} {3,6} {4,10} {5,8} {6,9} {7,10} {8,6}' -f `
                $p.Key, $p.Name, $p.Opacity, $p.Blur, $tint, $p.TintStrength, $p.Brightness, $acc, $p.Theme)
        }
    }
    Write-Host ''
    Write-Host '  用法：try-preset.ps1 <Key>     例：try-preset.ps1 liquid' -ForegroundColor Yellow
    Write-Host '        try-preset.ps1 -Cycle     逐个切换（每 6 秒一个）' -ForegroundColor Yellow
    Write-Host ''
}

function Load-Settings {
    if (-not (Test-Path $settingsPath)) {
        throw "找不到 $settingsPath —— 先启动一次程序让它生成默认配置"
    }
    $raw = [System.IO.File]::ReadAllText($settingsPath, [System.Text.Encoding]::UTF8)
    return $raw | ConvertFrom-Json
}

function Save-Settings($obj) {
    # 关键：必须无 BOM。带 BOM 的 settings.json 会让程序读不出第一个键。
    $json = $obj | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Stop-Clover {
    $procs = Get-Process CloverStart -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        Start-Sleep -Milliseconds 1500
    }
    # 清崩溃计数：连续 Stop-Process 会触发安全模式（2 分钟内 3 次）
    $guard = Join-Path $root 'config\crash-guard.json'
    if (Test-Path $guard) {
        $clean = [ordered]@{
            crashes = @(); safeMode = $false; running = $false
            runStarted = 0; crashRecordedThisRun = $false
            updated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        }
        [System.IO.File]::WriteAllText($guard, ($clean | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
    }
}

function Apply-Preset($p) {
    $s = Load-Settings

    # 逐个键赋值（PowerShell 的 PSCustomObject 加新属性要 Add-Member）
    $s | Add-Member -NotePropertyName 'glassPreset' -NotePropertyValue $p.Key -Force

    if ($p.Key -ne 'custom') {
        $s | Add-Member -NotePropertyName 'glassOpacity'      -NotePropertyValue $p.Opacity      -Force
        $s | Add-Member -NotePropertyName 'glassBlur'         -NotePropertyValue $p.Blur         -Force
        $s | Add-Member -NotePropertyName 'glassTint'         -NotePropertyValue $p.Tint         -Force
        $s | Add-Member -NotePropertyName 'glassTintStrength' -NotePropertyValue $p.TintStrength -Force
        $s | Add-Member -NotePropertyName 'glassBrightness'   -NotePropertyValue $p.Brightness   -Force
        $s | Add-Member -NotePropertyName 'glassAccent'       -NotePropertyValue $p.Accent       -Force

        if ($p.Theme -eq 'dark' -or $p.Theme -eq 'light') {
            $s | Add-Member -NotePropertyName 'theme' -NotePropertyValue $p.Theme -Force
        }
    }

    # 毛玻璃必须开着才看得见（opacity=0 等于关闭）
    if ($p.Key -ne 'custom' -and $p.Opacity -gt 0) {
        # 已经是该值就不动，避免无意义写盘
    }

    Save-Settings $s
    return $s
}

function Restart-Clover {
    if ($NoRestart) { return }
    Stop-Clover
    Start-Process $exe
    Start-Sleep -Seconds 10
    & $exe --show | Out-Null
    Start-Sleep -Milliseconds 2500
}

# ---- 主流程 ----
if ($Cycle) {
    Write-Host '逐个切换预设（每 6 秒一个，Ctrl+C 结束）...' -ForegroundColor Cyan
    foreach ($p in $presets) {
        if ($p.Key -eq 'custom') { continue }
        Write-Host ''
        Write-Host ('  >>> {0}（{1}）  opacity={2} blur={3} tint={4} tintStr={5} bright={6}' -f `
            $p.Name, $p.Key, $p.Opacity, $p.Blur, `
            $(if ([string]::IsNullOrEmpty($p.Tint)) { '强调色' } else { $p.Tint }), `
            $p.TintStrength, $p.Brightness) -ForegroundColor Green
        Apply-Preset $p | Out-Null
        Restart-Clover
        Start-Sleep -Seconds 6
    }
    Write-Host ''
    Write-Host '  循环结束。最后一个预设保留在 settings.json 里。' -ForegroundColor Yellow
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Preset)) {
    Show-Presets
    exit 0
}

$target = $presets | Where-Object { $_.Key -eq $Preset }
if (-not $target) {
    Write-Host "未知预设：$Preset" -ForegroundColor Red
    Show-Presets
    exit 1
}

Write-Host ''
Write-Host ('  切换到：{0}（{1}）' -f $target.Name, $target.Key) -ForegroundColor Green
$s = Apply-Preset $target
Write-Host ('  settings.json 已更新：glassPreset={0} glassOpacity={1} glassBlur={2} glassTint={3} tintStrength={4} brightness={5} theme={6}' -f `
    $s.glassPreset, $s.glassOpacity, $s.glassBlur, `
    $(if ([string]::IsNullOrEmpty($s.glassTint)) { '(空)' } else { $s.glassTint }), `
    $s.glassTintStrength, $s.glassBrightness, $s.theme) -ForegroundColor DarkGray

Restart-Clover
Write-Host '  已重启并弹出面板。看完了换下一个：try-preset.ps1 <Key>' -ForegroundColor Yellow
Write-Host ''
