[CmdletBinding()]
param(
    [string]$PayloadDirectory,
    [string]$OutputDirectory,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot 'dist\latest' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Path $OutputDirectory -Force)

if ([string]::IsNullOrWhiteSpace($PayloadDirectory)) {
    $PayloadDirectory = Join-Path $PSScriptRoot 'build\payload'
    $packageScript = Join-Path $PSScriptRoot 'PackageBridge.ps1'
    if ($SkipTests) { & $packageScript -SkipTests -OutputDirectory $PayloadDirectory }
    else { & $packageScript -OutputDirectory $PayloadDirectory }
}
$PayloadDirectory = [IO.Path]::GetFullPath($PayloadDirectory)
foreach ($required in @('Shim.exe', 'Shim.Updater.exe', 'drivers\vJoySetup_v2.2.2.0_Win10_Win11.exe', 'drivers\HidHide_1.5.230_x64.exe', 'runtime\x64\vJoyInterface.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadDirectory $required) -PathType Leaf)) { throw "Installer payload is missing $required. Build the Shim package first." }
}

$compilerCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($compiler)) { throw 'Inno Setup 6.7.3 or later is required. Install Inno Setup, then rerun BuildInstaller.ps1.' }

$previousPayload = $env:SHIM_SETUP_PAYLOAD
$previousRoot = $env:SHIM_PROJECT_ROOT
$previousOutput = $env:SHIM_SETUP_OUTPUT
try {
    $env:SHIM_SETUP_PAYLOAD = $PayloadDirectory
    $env:SHIM_PROJECT_ROOT = $PSScriptRoot
    $env:SHIM_SETUP_OUTPUT = $OutputDirectory
    & $compiler (Join-Path $PSScriptRoot 'installer\ShimSetup.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler failed with exit code $LASTEXITCODE." }
} finally {
    $env:SHIM_SETUP_PAYLOAD = $previousPayload
    $env:SHIM_PROJECT_ROOT = $previousRoot
    $env:SHIM_SETUP_OUTPUT = $previousOutput
}

$setupPath = Join-Path $OutputDirectory 'Shim-Setup-0.8.0.exe'
$hash = Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
('{0} *{1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($setupPath)) | Set-Content -LiteralPath ($setupPath + '.sha256') -Encoding ASCII
Write-Output "Built $setupPath"
