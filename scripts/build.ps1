# ==============================================================================
# VortexDownloader - Local Development Build and Test Script
# ==============================================================================
param(
    [string]$Configuration = "Debug",
    [switch]$RunApp,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  VortexDownloader Build Script ($Configuration)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

Push-Location $RepoRoot
try {
    Write-Host "`n[1/3] Restoring dependencies..." -ForegroundColor Yellow
    dotnet restore

    Write-Host "`n[2/3] Building solution..." -ForegroundColor Yellow
    dotnet build -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    if (-not $SkipTests) {
        Write-Host "`n[3/3] Running tests..." -ForegroundColor Yellow
        dotnet test -c $Configuration --no-build --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    Write-Host "`n[OK] Build and tests completed successfully!" -ForegroundColor Green

    if ($RunApp) {
        Write-Host "`nLaunching VortexDownloader UI..." -ForegroundColor Cyan
        dotnet run --project src/VortexDownloader.UI -c $Configuration --no-build
    }
}
finally {
    Pop-Location
}
