# Update an extracted release package. Never replace user configuration or content.
param(
    [Parameter(Mandatory=$true)][string]$SourcePackage,
    [Parameter(Mandatory=$true)][string]$TargetDirectory,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1.' }
$sourceRoot = (Resolve-Path -LiteralPath $SourcePackage).ProviderPath.TrimEnd('\')
$targetRoot = (Resolve-Path -LiteralPath $TargetDirectory).ProviderPath.TrimEnd('\')
if ($sourceRoot -eq $targetRoot -or $targetRoot -eq [IO.Path]::GetPathRoot($targetRoot).TrimEnd('\')) {
    throw 'Source must differ from target; a drive root cannot be an installation directory.'
}
$allowed = @('app\CloverStart.exe', 'res\brand.png', 'res\brand.ico', 'res\brand-light.png',
    'res\brand-dark.png', 'res\brand.svg', 'res\clover.png', 'res\clover.ico',
    'res\background-a1.png', 'res\background-cyangray.png')
function Get-UpdateHash($filePath) {
    $stream = [IO.File]::OpenRead($filePath)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}
function Get-SafeChild($basePath, $relative) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Unsafe package path: $relative"
    }
    $full = [IO.Path]::GetFullPath((Join-Path $basePath $relative))
    if (-not $full.StartsWith($basePath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes installation.' }
    $cursor = $full
    while ($cursor.Length -ge $basePath.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse points are not allowed in update paths: $cursor"
            }
        }
        if ($cursor -eq $basePath) { break }
        $cursor = Split-Path -Parent $cursor
    }
    return $full
}
$manifestPath = Get-SafeChild $sourceRoot 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.format -ne 1 -or $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or @($manifest.files).Count -eq 0) { throw 'Invalid release manifest.' }
$seen = @{}
$plan = @()
foreach ($entry in $manifest.files) {
    $relative = ([string]$entry.path).Replace('/', '\')
    if ($allowed -notcontains $relative -or $seen.ContainsKey($relative)) { throw "Unapproved or duplicate update path: $relative" }
    $seen[$relative] = $true
    $inputPath = Get-SafeChild $sourceRoot $relative
    $outputPath = Get-SafeChild $targetRoot $relative
    if (Test-Path -LiteralPath $outputPath -PathType Container) { throw "Target file is a directory: $relative" }
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Missing package file: $relative" }
    if ($entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-UpdateHash $inputPath) -ne $entry.sha256) {
        throw "Package checksum mismatch: $relative"
    }
    $plan += [pscustomobject]@{ path=$relative; source=$inputPath; target=$outputPath; sha256=$entry.sha256 }
}
if (-not $seen.ContainsKey('app\CloverStart.exe')) { throw 'Package has no main executable.' }
$packageVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $sourceRoot 'app\CloverStart.exe')).FileVersion
$actualVersion = [version]$packageVersion; $expectedVersion = [version]$manifest.version
if ($actualVersion.Major -ne $expectedVersion.Major -or $actualVersion.Minor -ne $expectedVersion.Minor -or
    $actualVersion.Build -ne $expectedVersion.Build -or $actualVersion.Revision -gt 0) { throw 'Executable version differs from manifest.' }
if (-not (Test-Path -LiteralPath (Get-SafeChild $targetRoot 'app\CloverStart.exe') -PathType Leaf)) { throw 'Target is not an existing installation.' }
if ($DryRun) { $plan | Select-Object path, target; return }

# Only ask the exact target executable to exit. Never kill other installations.
$targetExe = Join-Path $targetRoot 'app\CloverStart.exe'
foreach ($process in @(Get-Process -Name CloverStart -ErrorAction SilentlyContinue)) {
    try { $processPath = $process.Path } catch { throw 'Cannot verify a running CloverStart process; close it manually.' }
    if (-not [string]::Equals($processPath, $targetExe, [StringComparison]::OrdinalIgnoreCase)) { continue }
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(5000)) { throw 'Close the target application before updating.' }
}
$backupRelative = 'update-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$backupRoot = Get-SafeChild $targetRoot $backupRelative
New-Item -ItemType Directory -Path $backupRoot | Out-Null
$journal = @()
try {
    # Complete all backups before replacing any file.
    foreach ($item in $plan) {
        $old = Get-SafeChild $backupRoot $item.path
        $existed = Test-Path -LiteralPath $item.target -PathType Leaf
        if ($existed) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $old) | Out-Null
            Copy-Item -LiteralPath $item.target -Destination $old
            if ((Get-UpdateHash $old) -ne (Get-UpdateHash $item.target)) { throw 'Backup verification failed.' }
        }
        $journal += [pscustomobject]@{ path=$item.path; target=$item.target; backup=$old; existed=$existed; applied=$false }
    }
    $journal | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $backupRoot 'journal.json') -Encoding UTF8
    for ($index = 0; $index -lt $plan.Count; $index++) {
        $item = $plan[$index]
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $item.target) | Out-Null
        $temporary = $item.target + '.' + [guid]::NewGuid().ToString('N') + '.update'
        try {
            Copy-Item -LiteralPath $item.source -Destination $temporary
            if ((Get-UpdateHash $temporary) -ne $item.sha256) { throw 'Staged file checksum mismatch.' }
            if ($journal[$index].existed) { [IO.File]::Replace($temporary, $item.target, ($journal[$index].backup + '.replacement')) }
            else { [IO.File]::Move($temporary, $item.target) }
            $journal[$index].applied = $true
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
        }
    }
    Write-Host "Updated to $($manifest.version). Previous program files: $backupRoot"
    Write-Host 'Configuration, collected desktop content, custom backgrounds and autostart are unchanged.'
} catch {
    $updateFailure = $_
    $rollbackErrors = @()
    foreach ($entry in $journal) {
        if (-not $entry.applied) { continue }
        try {
            if ($entry.existed) { Copy-Item -LiteralPath $entry.backup -Destination $entry.target -Force }
            elseif (Test-Path -LiteralPath $entry.target -PathType Leaf) { Remove-Item -LiteralPath $entry.target }
        } catch { $rollbackErrors += $_.Exception.Message }
    }
    if ($rollbackErrors.Count -gt 0) { throw "Update failed; restore program files from $backupRoot. $($rollbackErrors -join '; ')" }
    throw $updateFailure
}
