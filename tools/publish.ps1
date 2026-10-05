<#
.SYNOPSIS
  Builds a release of Interview Coach: one self-contained InterviewCoach.App.exe (no .NET install needed on the other machine) and a zip of it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Version 1.0.0 -Runtime win-arm64
#>
param(
    [string] $Version = "1.0.0",
    [string] $Runtime = "win-x64",
    [switch] $SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipTests) {
    Write-Host "Running the tests first..." -ForegroundColor Cyan
    dotnet test InterviewCoach.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "The tests failed, so nothing was published." }
}

$name = "InterviewCoach-$Version-$Runtime"
$out = Join-Path $root "dist\$name"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Publishing $name..." -ForegroundColor Cyan
dotnet publish src/InterviewCoach.App -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false `
    -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

$exe = Get-Item (Join-Path $out "InterviewCoach.App.exe")
Write-Host ""
Write-Host ("Done. {0} ({1:N0} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
Write-Host ("Zip:  {0}" -f $zip) -ForegroundColor Green
Write-Host "Keep the Prompts folder next to the exe (the prompts can be edited there); the app falls back to its built-in copies if it is missing."
