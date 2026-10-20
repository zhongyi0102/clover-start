# 生成 src\PinyinData.cs：汉字 -> 无调拼音 映射表。
#
# 数据来源：Windows 自带的微软拼音单字库
#   C:\Windows\System32\chs_singlechar_pinyin.dat
#   （2 字节 UTF-16 汉字 + 变长小写 ASCII 拼音）
#
# 这个文件把 1.6 万个汉字压成两个 int 数组（码位增量 + 音节下标），
# 编译进程序后用于搜索框的拼音匹配（全拼 weixin / 首字母 wx）。
#
# 系统词库缺失时退回内置精简字表，保证脚本在任何机器上都能跑出结果。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-pinyin.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outFile = Join-Path $root 'src\PinyinData.cs'
$datFile = Join-Path $env:WINDIR 'System32\chs_singlechar_pinyin.dat'

# ---------------- 1. 读取拼音数据 ----------------
$table = @{}   # char -> pinyin（同字多音时保留第一个）

if (Test-Path $datFile) {
    $bytes = [System.IO.File]::ReadAllBytes($datFile)
    $i = 0
    while ($i + 4 -lt $bytes.Length) {
        $code = [System.BitConverter]::ToUInt16($bytes, $i)
        $isHan = (($code -ge 0x4E00 -and $code -le 0x9FFF) -or $code -eq 0x3007)
        if ($isHan) {
            $ch = [char]$code
            $j = $i + 2
            $sb2 = New-Object System.Text.StringBuilder
            while ($j -lt $bytes.Length -and $bytes[$j] -ge 0x61 -and $bytes[$j] -le 0x7A) {
                [void]$sb2.Append([char]$bytes[$j]); $j++
            }
            $py = $sb2.ToString()
            if ($py.Length -ge 1 -and $py.Length -le 7) {
                if (-not $table.ContainsKey($ch)) { $table[$ch] = $py }
                $i = $j
                continue
            }
        }
        $i++
    }
    "数据来源: $datFile"
} else {
    Write-Warning "找不到 $datFile，使用内置精简字表"
}

# ---------------- 2. 内置兜底字表（仅在系统词库缺失时使用） ----------------
if ($table.Count -eq 0) {
    $fallback = @{
        'wei'='微为位未委卫味维'; 'xin'='新心信'; 'jian'='见件建检简健键渐监减剑间';
        'ying'='应英影营硬映'; 'chong'='重冲充虫宠'; 'she'='设社射舍蛇';
        'zhi'='只之制知直指纸治质支职置'; 'qi'='起其气期七器汽齐奇启';
        'dong'='东动懂洞冬冻栋'; 'teng'='疼腾'; 'xun'='训迅寻讯'
    }
    foreach ($py in $fallback.Keys) {
        foreach ($c in $fallback[$py].ToCharArray()) {
            if (-not $table.ContainsKey($c)) { $table[$c] = $py }
        }
    }
}

# ---------------- 3. 音节表（去声调） ----------------
$syllableSet = New-Object System.Collections.Generic.SortedSet[string]
foreach ($py in $table.Values) { [void]$syllableSet.Add($py) }
$syllables = @($syllableSet)
$syllIndex = @{}
for ($k = 0; $k -lt $syllables.Count; $k++) { $syllIndex[$syllables[$k]] = $k }

# ---------------- 4. 生成紧凑数据 ----------------
# 按码位升序，用「相对前一个字的码位增量」+「音节下标」两个 int 数组表达
$sorted = $table.GetEnumerator() | Sort-Object { [int]$_.Key }
$codes = New-Object System.Collections.Generic.List[int]
$pys = New-Object System.Collections.Generic.List[int]
$prev = 0
foreach ($kv in $sorted) {
    $code = [int]$kv.Key
    $codes.Add($code - $prev)
    $pys.Add($syllIndex[$kv.Value])
    $prev = $code
}

function Write-IntArray($sb, $name, $values, $comment) {
    [void]$sb.AppendLine("        /// <summary>$comment</summary>")
    [void]$sb.AppendLine("        public static readonly int[] $name = new int[]")
    [void]$sb.AppendLine('        {')
    $line = '            '
    for ($k = 0; $k -lt $values.Count; $k++) {
        $piece = $values[$k].ToString() + ','
        if (($line.Length + $piece.Length) -gt 110) {
            [void]$sb.AppendLine($line.TrimEnd())
            $line = '            '
        }
        $line += $piece
    }
    if ($line.Trim().Length -gt 0) { [void]$sb.AppendLine($line.TrimEnd()) }
    [void]$sb.AppendLine('        };')
    [void]$sb.AppendLine()
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// 由 tools\make-pinyin.ps1 自动生成，请勿手工编辑。')
[void]$sb.AppendLine('// 汉字 -> 无调拼音映射，用于面板搜索的拼音匹配（全拼与首字母）。')
[void]$sb.AppendLine('// 数据来源：Windows 微软拼音单字库 chs_singlechar_pinyin.dat')
[void]$sb.AppendLine('namespace Clover')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    static class PinyinData')
[void]$sb.AppendLine('    {')
[void]$sb.AppendLine('        /// <summary>所有无调音节（升序）。</summary>')
[void]$sb.AppendLine('        public static readonly string[] Syllables = new string[]')
[void]$sb.AppendLine('        {')
$line = '            '
for ($k = 0; $k -lt $syllables.Count; $k++) {
    $piece = '"' + $syllables[$k] + '",'
    if (($line.Length + $piece.Length) -gt 110) {
        [void]$sb.AppendLine($line.TrimEnd())
        $line = '            '
    }
    $line += $piece
}
if ($line.Trim().Length -gt 0) { [void]$sb.AppendLine($line.TrimEnd()) }
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine()

Write-IntArray $sb 'CodeDelta' $codes '汉字码位相对前一个字的增量（升序），与 PinyinIndex 一一对应。'
Write-IntArray $sb 'PinyinIndex' $pys '每个汉字对应的 Syllables 下标。'

[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')

[System.IO.File]::WriteAllText($outFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
"生成 $outFile"
"音节数: $($syllables.Count)   汉字数: $($sorted.Count)"
"文件大小: " + [math]::Round((Get-Item $outFile).Length / 1KB, 1) + " KB"
