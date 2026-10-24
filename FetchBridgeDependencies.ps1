[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$dependencies = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bridge\dependencies.lock.json') -Raw | ConvertFrom-Json
$cachePath = Join-Path $PSScriptRoot 'third-party\rig-bridge'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot 'dist\bridge' }
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$driverPath = Join-Path $outputDirectory 'drivers'
$runtimePath = Join-Path $outputDirectory 'runtime\x64'
$licensePath = Join-Path $outputDirectory 'licenses'
foreach ($directory in @($cachePath, $driverPath, $runtimePath, $licensePath)) { [void](New-Item -ItemType Directory -Path $directory -Force) }
function Get-PinnedFile([string]$Uri, [string]$Destination, [string]$Sha256) {
    if (-not (Test-Path -LiteralPath $Destination)) { Invoke-WebRequest -Uri $Uri -OutFile $Destination -UseBasicParsing }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) { throw "SHA-256 mismatch for $Destination. Keep the file for inspection and download a clean copy." }
}
foreach ($dependency in @($dependencies.vJoy, $dependencies.HidHide)) {
    $installerPath = Join-Path $cachePath $dependency.installer
    Get-PinnedFile $dependency.url $installerPath $dependency.sha256
    $signature = Get-AuthenticodeSignature -LiteralPath $installerPath
    if ($signature.Status -ne 'Valid') { throw "The installer signature is not valid: $installerPath ($($signature.Status))." }
    $bundledInstaller = Join-Path $driverPath $dependency.installer
    # Keep a matching installer in place, including when Windows is still running it.
    if (-not (Test-Path -LiteralPath $bundledInstaller) -or (Get-FileHash -LiteralPath $bundledInstaller -Algorithm SHA256).Hash -ne $dependency.sha256) {
        Copy-Item -LiteralPath $installerPath -Destination $bundledInstaller -Force
    }
    Write-Output "Verified signed installer: $($dependency.installer)"
}
$sdkPath = Join-Path $cachePath 'vJoy-SDK-v2.2.2.0.zip'
Get-PinnedFile $dependencies.vJoy.sdkUrl $sdkPath $dependencies.vJoy.sdkSha256
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($sdkPath)
try {
    $entry = $archive.GetEntry('SDK/lib/x64/vJoyInterface.dll')
    if ($null -eq $entry) { throw 'The pinned SDK does not contain the expected x64 library.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $runtimePath 'vJoyInterface.dll'), $true)
} finally { $archive.Dispose() }
Invoke-WebRequest -Uri $dependencies.vJoy.licenseUrl -OutFile (Join-Path $licensePath 'vJoy-MIT.txt') -UseBasicParsing
Invoke-WebRequest -Uri $dependencies.HidHide.licenseUrl -OutFile (Join-Path $licensePath 'HidHide-MIT.txt') -UseBasicParsing
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge\dependencies.lock.json') -Destination (Join-Path $outputDirectory 'dependencies.lock.json') -Force
Write-Output 'Bundled installers and x64 SDK prepared. No installers were executed.'
