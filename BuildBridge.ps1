[CmdletBinding()]
param([switch]$Test, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot 'dist\bridge' }
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Path $outputDirectory -Force)
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'bridge\src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
$brandGraphic = Join-Path $PSScriptRoot 'assets\shim-app-mark-transparent.png'
if (-not (Test-Path -LiteralPath $brandGraphic -PathType Leaf)) { throw "Shim brand graphic not found: $brandGraphic" }
$resources = @("/resource:$brandGraphic,Shim.Graphics.Brand.png")
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /langversion:5 "/out:$(Join-Path $outputDirectory 'Shim.exe')" "/win32manifest:$(Join-Path $PSScriptRoot 'bridge\src\app.manifest')" @references @resources @sources
if ($LASTEXITCODE -ne 0) { throw 'Bridge compilation failed.' }
Write-Output "Built $(Join-Path $outputDirectory 'Shim.exe')"
$updaterSource = Join-Path $PSScriptRoot 'bridge\updater\Updater.cs'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warn:4 /langversion:5 "/out:$(Join-Path $outputDirectory 'Shim.Updater.exe')" "/win32manifest:$(Join-Path $PSScriptRoot 'bridge\src\app.manifest')" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $updaterSource
if ($LASTEXITCODE -ne 0) { throw 'Shim update helper compilation failed.' }
Write-Output "Built $(Join-Path $outputDirectory 'Shim.Updater.exe')"
if ($Test) {
    $testPath = Join-Path $outputDirectory 'Shim.Tests.exe'
    $testSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'bridge\tests') -Filter '*.cs' | ForEach-Object FullName)
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /warn:4 /langversion:5 "/out:$testPath" /main:RigBridge.Tests.TestProgram @references @resources @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Bridge test compilation failed.' }
    & $testPath
    if ($LASTEXITCODE -ne 0) { throw 'Bridge tests failed.' }
}
