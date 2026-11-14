# Portable package with a verified program-only update manifest.
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1.' }
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw -Encoding ASCII).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid VERSION.' }
if (-not $SkipBuild) {
    if (Get-Process -Name CloverStart -ErrorAction SilentlyContinue) { throw 'Close CloverStart before building.' }
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & (Join-Path $root 'tests\run-tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
$exe = Join-Path $root 'app\CloverStart.exe'
$evidence = Join-Path $root 'tests\bin\test-summary.json'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $evidence)) { throw 'Build and full test evidence are required.' }
$summary = Get-Content -LiteralPath $evidence -Raw -Encoding UTF8 | ConvertFrom-Json
$exeInfo = Get-Item -LiteralPath $exe
$actualVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
$expectedVersion = [version]$version
if ($actualVersion.Major -ne $expectedVersion.Major -or $actualVersion.Minor -ne $expectedVersion.Minor -or
    $actualVersion.Build -ne $expectedVersion.Build -or $actualVersion.Revision -gt 0) { throw 'Executable version differs from VERSION.' }
if ($summary.mode -ne 'full' -or $summary.failed -ne 0 -or $summary.suites -lt 1 -or @($summary.results | Where-Object { $_.status -ne 'passed' }).Count -gt 0) { throw 'Full tests must pass.' }
if ([datetime]$summary.startedAt -lt $exeInfo.LastWriteTime -or [datetime]$summary.generatedAt -lt $exeInfo.LastWriteTime) { throw 'Test evidence predates the executable.' }
$inputs = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Filter '*.cs') +
    @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $root 'tools') -File -Filter '*.ps1') +
    @(Get-ChildItem -LiteralPath (Join-Path $root 'res') -File) + @(Get-Item -LiteralPath (Join-Path $root 'build.ps1'))
if (@($inputs | Where-Object { $_.LastWriteTime -gt [datetime]$summary.startedAt }).Count -gt 0) { throw 'Source or tests changed after the test run began.' }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$packageName = "CloverStart-v$version"
$package = Join-Path $dist $packageName
$staging = Join-Path $dist ($packageName + '.staging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
$resources = @('brand.png','brand.ico','brand-light.png','brand-dark.png','brand.svg','clover.png','clover.ico','background-a1.png','background-cyangray.png')
$programFiles = @('app\CloverStart.exe') + @($resources | ForEach-Object { 'res\' + $_ })
$documentation = @('VERSION','LICENSE','README.md','THIRD-PARTY-NOTICES.md','licenses\Apache-2.0.txt',
    'docs\PRODUCT-RULES.md','docs\UPGRADE.md','docs\RELEASE-CHECKLIST.md','tools\update.ps1',
    'res\screenshot-light.png','res\screenshot-dark.png')
foreach ($relative in ($programFiles + $documentation)) {
    $inputPath = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Missing release input: $relative" }
    $outputPath = Join-Path $staging $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath) | Out-Null
    Copy-Item -LiteralPath $inputPath -Destination $outputPath
}
$files = @($programFiles | ForEach-Object {
    [pscustomobject]@{ path=$_.Replace('\','/'); sha256=(Get-FileHash -LiteralPath (Join-Path $staging $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[pscustomobject]@{ format=1; version=$version; files=$files } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $staging 'release-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath $evidence -Destination (Join-Path $staging 'test-summary.json')
# These are generated package artifacts, not workspace source edits.
[IO.File]::WriteAllText((Join-Path $staging 'Launch.bat'), "@echo off`r`ncd /d `"%~dp0`"`r`nstart `"`" `"app\CloverStart.exe`"`r`n", [Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $staging 'Enable-autostart.bat'), "@echo off`r`ncd /d `"%~dp0`"`r`n`"app\CloverStart.exe`" --enable-autostart`r`nif errorlevel 1 pause`r`n", [Text.Encoding]::ASCII)
Copy-Item -LiteralPath (Join-Path $root 'docs\UPGRADE.md') -Destination (Join-Path $staging 'UPGRADE.md')
$backupSuffix = '.previous-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
foreach ($oldPath in @($package, ($package + '.zip'), ($package + '.zip.sha256'), (Join-Path $dist 'CloverStart.exe'), (Join-Path $dist 'CloverStart.exe.sha256'))) {
    $resolved = [IO.Path]::GetFullPath($oldPath)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($dist) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release output escapes dist.' }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release output cannot be a reparse point.' }
        Move-Item -LiteralPath $resolved -Destination ($resolved + $backupSuffix)
    }
}
Move-Item -LiteralPath $staging -Destination $package
$zipPath = $package + '.zip'
Compress-Archive -LiteralPath $package -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zipPath + '.sha256'), "$hash  $packageName.zip`n", [Text.Encoding]::ASCII)
$single = Join-Path $dist 'CloverStart.exe'
Copy-Item -LiteralPath $exe -Destination $single
$singleHash = (Get-FileHash -LiteralPath $single -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($single + '.sha256'), "$singleHash  CloverStart.exe`n", [Text.Encoding]::ASCII)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    foreach ($relative in ($programFiles + $documentation + @('release-manifest.json','test-summary.json'))) {
        if ($names -notcontains ($packageName + '/' + $relative.Replace('\','/'))) { throw "Archive is missing $relative" }
    }
} finally { $archive.Dispose() }
Write-Host "Verified portable package: $zipPath"
Write-Host "SHA256: $hash"
Write-Host 'No public release was created. Previous same-version outputs are preserved.'
