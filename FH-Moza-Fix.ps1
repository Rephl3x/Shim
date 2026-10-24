[CmdletBinding()]
param(
    [switch]$Administrator,
    [string]$Diagnose
)

$ErrorActionPreference = 'Stop'
$appPath = Join-Path $PSScriptRoot 'FH-Moza-Fix.exe'
if (-not (Test-Path -LiteralPath $appPath)) { $appPath = Join-Path $PSScriptRoot 'dist\FH-Moza-Fix.exe' }
if (-not (Test-Path -LiteralPath $appPath)) { throw 'FH-Moza-Fix.exe was not found. Extract the release ZIP, or run .\Build.ps1 first.' }
if ($Diagnose) {
    $diagnosticPath = [IO.Path]::GetFullPath($Diagnose)
    if ($diagnosticPath.Contains('"')) { throw 'The output path cannot contain quotes.' }
    $process = Start-Process -FilePath $appPath -ArgumentList @('--diagnose', ('"' + $diagnosticPath + '"')) -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw 'Diagnostics failed. See %LOCALAPPDATA%\FHMozaFix\startup-error.txt.' }
    Write-Output "Read-only device report saved to $diagnosticPath"
} elseif ($Administrator) {
    Start-Process -FilePath $appPath -Verb RunAs
} else {
    Start-Process -FilePath $appPath
}
