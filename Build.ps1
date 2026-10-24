[CmdletBinding()]
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The built-in .NET Framework C# compiler was not found. Build on Windows 10/11 x64 with .NET Framework 4.8.'
}
$outputDirectory = Join-Path $PSScriptRoot 'dist'
[void](New-Item -ItemType Directory -Path $outputDirectory -Force)
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll')
$appPath = Join-Path $outputDirectory 'FH-Moza-Fix.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /langversion:5 "/out:$appPath" "/win32manifest:$(Join-Path $PSScriptRoot 'src\app.manifest')" @references @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Write-Output "Built $appPath"
if ($Test) {
    $testPath = Join-Path $outputDirectory 'FH-Moza-Fix.Tests.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /warn:4 /langversion:5 "/out:$testPath" /main:FHMozaFix.Tests.Program @references @sourceFiles (Join-Path $PSScriptRoot 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testPath
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
