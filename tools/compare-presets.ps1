# 逐个切换预设并采集主色（视觉验证用）
# 只用于验收，不参与正式测试（需要真实桌面会话）

$ErrorActionPreference = 'Stop'
# 仓库根由脚本自身位置推出（tools\ 的上一级），不写死盘符。
# 这样 clone 到任意路径、或在 worktree 里跑都能用。
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'app\CloverStart.exe'
$settingsPath = Join-Path $root 'config\settings.json'
$tool = Join-Path $root 'tools\try-preset.ps1'

Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Capture-Panel($tag) {
    # 面板可能要重试几次才出来（重启后窗口创建有延迟）
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        & $exe --show | Out-Null
        Start-Sleep -Milliseconds 2500
        $re0 = [System.Windows.Automation.AutomationElement]::RootElement
        $wc0 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "四叶草面板")
        $w0 = $re0.FindFirst([System.Windows.Automation.TreeScope]::Children, $wc0)
        if ($w0) { break }
    }
    Start-Sleep -Milliseconds 800

    $re = [System.Windows.Automation.AutomationElement]::RootElement
    $wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '四叶草面板')
    $win = $re.FindFirst([System.Windows.Automation.TreeScope]::Children, $wc)
    if (-not $win) { return $null }
    $wr = $win.Current.BoundingRectangle
    $x = [int]$wr.X; $y = [int]$wr.Y; $w = [int]$wr.Width; $h = [int]$wr.Height

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save("E:\AI\demo\preset-$tag.png", [System.Drawing.Imaging.ImageFormat]::Png)

    # 直方图（量化 32 级）
    $hist = @{}
    for ($yy = 0; $yy -lt $h; $yy += 4) {
        for ($xx = 0; $xx -lt $w; $xx += 4) {
            $c = $bmp.GetPixel($xx, $yy)
            $key = "#{0:X2}{1:X2}{2:X2}" -f ([int]($c.R/32)*32), ([int]($c.G/32)*32), ([int]($c.B/32)*32)
            if ($hist.ContainsKey($key)) { $hist[$key]++ } else { $hist[$key] = 1 }
        }
    }
    $tot = ($hist.Values | Measure-Object -Sum).Sum
    $top = $hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1

    # 全图平均亮度
    $sumR = 0.0; $sumG = 0.0; $sumB = 0.0; $n = 0
    for ($yy = 0; $yy -lt $h; $yy += 4) {
        for ($xx = 0; $xx -lt $w; $xx += 4) {
            $c = $bmp.GetPixel($xx, $yy)
            $sumR += $c.R; $sumG += $c.G; $sumB += $c.B; $n++
        }
    }
    $bmp.Dispose()

    return [PSCustomObject]@{
        Tag = $tag
        Dominant = $top.Key
        DomPct = [math]::Round($top.Value / $tot * 100, 1)
        AvgR = [math]::Round($sumR / $n, 0)
        AvgG = [math]::Round($sumG / $n, 0)
        AvgB = [math]::Round($sumB / $n, 0)
        Lum = [math]::Round(0.299*($sumR/$n) + 0.587*($sumG/$n) + 0.114*($sumB/$n), 0)
    }
}

$results = @()
foreach ($k in @('classic', 'frosted', 'dark', 'light', 'liquid')) {
    Write-Host "  切换 $k ..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $tool $k 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    $r = Capture-Panel $k
    if ($r) { $results += $r }
}

Write-Host ''
Write-Host '=== 预设视觉对比（真实截图采样）===' -ForegroundColor Cyan
Write-Host ''
Write-Host ('  {0,-10} {1,-12} {2,6} {3,7} {4,7} {5,7}' -f 'Key', '主色', '占比%', 'AvgR', 'AvgG', 'AvgB')
Write-Host ('  ' + ('-' * 58))
foreach ($r in $results) {
    Write-Host ('  {0,-10} {1,-12} {2,6} {3,7} {4,7} {5,7}   亮度 {6}' -f `
        $r.Tag, $r.Dominant, $r.DomPct, $r.AvgR, $r.AvgG, $r.AvgB, $r.Lum)
}
Write-Host ''

# 差异检查
Write-Host '=== 差异检查 ===' -ForegroundColor Cyan
for ($i = 0; $i -lt $results.Count; $i++) {
    for ($j = $i + 1; $j -lt $results.Count; $j++) {
        $a = $results[$i]; $b = $results[$j]
        $dr = [math]::Abs($a.AvgR - $b.AvgR)
        $dg = [math]::Abs($a.AvgG - $b.AvgG)
        $db = [math]::Abs($a.AvgB - $b.AvgB)
        $diff = $dr + $dg + $db
        $flag = if ($diff -ge 20) { '✅' } else { '⚠️' }
        Write-Host ('  {0} {1,-9} vs {2,-9}  RGB 差异合计 {3,4}  (R{4,3} G{5,3} B{6,3})' -f `
            $flag, $a.Tag, $b.Tag, $diff, $dr, $dg, $db)
    }
}
