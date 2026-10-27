[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build.ps1')
$outputDirectory = Join-Path $PSScriptRoot 'build\legacy'
$distributionDirectory = Join-Path $PSScriptRoot 'dist\latest'
[void](New-Item -ItemType Directory -Path $distributionDirectory -Force)
$zipPath = Join-Path $distributionDirectory 'FH-Moza-Fix-portable.zip'
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
$hashes | Set-Content -LiteralPath (Join-Path $distributionDirectory 'FH-Moza-Fix-SHA256SUMS.txt') -Encoding ASCII
Write-Output "Share $zipPath"
