[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build.ps1') -Test
$outputDirectory = Join-Path $PSScriptRoot 'dist'
$zipPath = Join-Path $outputDirectory 'FH-Moza-Fix-portable.zip'
$packageFiles = @(
    (Join-Path $outputDirectory 'FH-Moza-Fix.exe')
    (Join-Path $PSScriptRoot 'README.md')
    (Join-Path $PSScriptRoot 'FH-Moza-Fix.ps1')
    (Join-Path $PSScriptRoot 'Build.ps1')
    (Join-Path $PSScriptRoot 'Package.ps1')
    (Join-Path $PSScriptRoot 'src')
    (Join-Path $PSScriptRoot 'tests')
)
Compress-Archive -LiteralPath $packageFiles -DestinationPath $zipPath -Force
$hashes = foreach ($file in @($zipPath, (Join-Path $outputDirectory 'FH-Moza-Fix.exe'))) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    '{0} *{1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file)
}
$hashes | Set-Content -LiteralPath (Join-Path $outputDirectory 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "Share $zipPath"
