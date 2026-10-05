<#
.SYNOPSIS
  Builds a release of Interview Coach: one self-contained InterviewCoach.App.exe (no .NET install needed on the other machine), a zip of it,
  and with -Installer a Windows setup program (InterviewCoach-Setup-<version>.exe) made with Inno Setup.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Installer
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Version 1.2.0 -Installer -SkipTests

  Inno Setup is needed for -Installer:  winget install JRSoftware.InnoSetup
#>
param(
    [string] $Version = "1.0.0",
    [string] $Runtime = "win-x64",
    [switch] $Installer,
    [switch] $SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Find Inno Setup first, so a missing tool is reported before the long build, not after it.
$iscc = $null
if ($Installer) {
    $candidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }
    $iscc = $candidates | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup was not found. Install it with:  winget install JRSoftware.InnoSetup" }
}

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

# The licenses travel with the program.
Copy-Item (Join-Path $root "LICENSE"), (Join-Path $root "THIRD-PARTY-NOTICES.md") -Destination $out
Copy-Item (Join-Path $root "licenses") -Destination (Join-Path $out "licenses") -Recurse

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

$exe = Get-Item (Join-Path $out "InterviewCoach.App.exe")
Write-Host ""
Write-Host ("Done. {0} ({1:N0} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
Write-Host ("Zip:  {0}" -f $zip) -ForegroundColor Green

if ($Installer) {
    Write-Host "Building the installer with Inno Setup..." -ForegroundColor Cyan
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$out" "/DOutputDir=$(Join-Path $root 'dist')" (Join-Path $root "installer\InterviewCoach.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
    $setup = Get-Item (Join-Path $root "dist\InterviewCoach-Setup-$Version.exe")
    Write-Host ("Installer: {0} ({1:N0} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
}

Write-Host "Keep the Prompts folder next to the exe (the prompts can be edited there); the app falls back to its built-in copies if it is missing."
