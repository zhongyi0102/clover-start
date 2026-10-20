# 生成 src\PinyinData.cs：汉字 -> 无调拼音 映射表。
#
# 数据来源：pinyin-pro（https://github.com/zh-lx/pinyin-pro）
#   MIT License, Copyright (c) 2022-present zh-lx
#   取它的单字字典 dist/esm/data/dict1.mjs，用 tools\pinyin-data\extract.mjs
#   导出成 pinyin-table.txt（每行「码位 无调拼音」），再由本脚本压成 C# 数组。
#
# 之所以换掉原来的 Windows 系统字库（chs_singlechar_pinyin.dat）：那是微软的
# 专有数据，把它派生的字表编译进 MIT 项目再公开分发有版权风险。
#
# 这个文件把两万多个汉字压成两个 int 数组（码位增量 + 音节下标），
# 编译进程序后用于搜索框的拼音匹配（全拼 weixin / 首字母 wx）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-pinyin.ps1
# 需要先准备好 tools\pinyin-data\pinyin-table.txt（生成方式见该目录 README）。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outFile = Join-Path $root 'src\PinyinData.cs'
$tableFile = Join-Path $PSScriptRoot 'pinyin-data\pinyin-table.txt'

# ---------------- 1. 读取拼音数据 ----------------
$table = [System.Collections.Generic.Dictionary[int, string]]::new()

if (-not (Test-Path $tableFile)) {
    throw "找不到 $tableFile。先按 tools\pinyin-data\README.md 生成它，再跑本脚本。"
}

# 每行「码位 拼音」，如：20320 ni
foreach ($line in Get-Content $tableFile -Encoding UTF8) {
    $t = $line.Trim()
    if ($t.Length -eq 0 -or $t.StartsWith('#')) { continue }
    $sp = $t.IndexOf(' ')
    if ($sp -le 0) { continue }
    $code = 0
    if (-not [int]::TryParse($t.Substring(0, $sp), [ref]$code)) { continue }
    $py = $t.Substring($sp + 1).Trim()
    if ($py -notmatch '^[a-z]+$') { continue }
    if ($py.Length -lt 1 -or $py.Length -gt 7) { continue }
    if (-not $table.ContainsKey($code)) { $table[$code] = $py }
}

if ($table.Count -eq 0) { throw "拼音表为空：$tableFile" }
"数据来源: $tableFile"

# ---------------- 2. 音节表（去声调） ----------------
$syllableSet = New-Object System.Collections.Generic.SortedSet[string]
foreach ($py in $table.Values) { [void]$syllableSet.Add($py) }
$syllables = @($syllableSet)
$syllIndex = @{}
for ($k = 0; $k -lt $syllables.Count; $k++) { $syllIndex[$syllables[$k]] = $k }

# ---------------- 3. 生成紧凑数据 ----------------
# 按码位升序，用「相对前一个字的码位增量」+「音节下标」两个 int 数组表达
$sorted = $table.GetEnumerator() | Sort-Object { $_.Key }
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
[void]$sb.AppendLine('// 数据来源：pinyin-pro（MIT License, Copyright (c) 2022-present zh-lx）')
[void]$sb.AppendLine('//   https://github.com/zh-lx/pinyin-pro')
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
