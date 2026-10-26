[CmdletBinding()]
param([string]$OutputDirectory, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot 'dist\bridge' }
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $PSScriptRoot 'FetchBridgeDependencies.ps1') -OutputDirectory $outputDirectory
if ($SkipTests) { & (Join-Path $PSScriptRoot 'BuildBridge.ps1') -OutputDirectory $outputDirectory }
else { & (Join-Path $PSScriptRoot 'BuildBridge.ps1') -Test -OutputDirectory $outputDirectory }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge\README.md') -Destination (Join-Path $outputDirectory 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge\THIRD-PARTY-NOTICES.md') -Destination (Join-Path $outputDirectory 'THIRD-PARTY-NOTICES.md') -Force
$packageEntries = [Collections.Generic.List[object]]::new()
foreach ($name in @('Shim.exe', 'Shim.Updater.exe', 'README.md', 'THIRD-PARTY-NOTICES.md', 'dependencies.lock.json')) {
    $packageEntries.Add(@{Source=(Join-Path $outputDirectory $name); Entry=$name})
}
$packageEntries.Add(@{Source=(Join-Path $PSScriptRoot 'assets\shim-app-mark-transparent.png'); Entry='assets/shim-app-mark-transparent.png'})
foreach ($folder in @('drivers', 'runtime', 'licenses')) {
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $outputDirectory $folder) -File -Recurse)) {
        $packageEntries.Add(@{Source=$file.FullName; Entry=$file.FullName.Substring($outputDirectory.Length + 1).Replace('\', '/')})
    }
}
foreach ($name in @('BuildBridge.ps1', 'FetchBridgeDependencies.ps1', 'PackageBridge.ps1')) {
    $packageEntries.Add(@{Source=(Join-Path $PSScriptRoot $name); Entry=$name})
}
$sourceRoot = Join-Path $PSScriptRoot 'bridge'
foreach ($file in (Get-ChildItem -LiteralPath $sourceRoot -File -Recurse)) {
    $packageEntries.Add(@{Source=$file.FullName; Entry=('bridge/' + $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/'))})
}
$hashLines = foreach ($entry in $packageEntries) {
    '{0} *{1}' -f (Get-FileHash -LiteralPath $entry.Source -Algorithm SHA256).Hash.ToLowerInvariant(), $entry.Entry
}
$checksumsPath = Join-Path $outputDirectory 'SHA256SUMS.txt'
$hashLines | Set-Content -LiteralPath $checksumsPath -Encoding ASCII
$packageEntries.Add(@{Source=$checksumsPath; Entry='SHA256SUMS.txt'})
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $PSScriptRoot 'dist\Shim-0.7.0.zip'
New-Item -ItemType Directory -Path (Split-Path -Parent $zipPath) -Force | Out-Null
# Overwrite only this exact output file. No recursive cleanup is used.
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $packageEntries) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Source, $entry.Entry, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose(); $stream.Dispose() }
$zipHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
('{0} *{1}' -f $zipHash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($zipPath)) | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ASCII
Write-Output "Share $zipPath"
