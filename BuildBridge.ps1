[CmdletBinding()]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDirectory = Join-Path $PSScriptRoot 'dist\bridge'
[void](New-Item -ItemType Directory -Path $outputDirectory -Force)
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'bridge\src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll')
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /langversion:5 "/out:$(Join-Path $outputDirectory 'FH-Rig-Bridge.exe')" "/win32manifest:$(Join-Path $PSScriptRoot 'bridge\src\app.manifest')" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Bridge compilation failed.' }
Write-Output "Built $(Join-Path $outputDirectory 'FH-Rig-Bridge.exe')"
if ($Test) {
    $testPath = Join-Path $outputDirectory 'FH-Rig-Bridge.Tests.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /warn:4 /langversion:5 "/out:$testPath" /main:RigBridge.Tests.TestProgram @references @sources (Join-Path $PSScriptRoot 'bridge\tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Bridge test compilation failed.' }
    & $testPath
    if ($LASTEXITCODE -ne 0) { throw 'Bridge tests failed.' }
}
