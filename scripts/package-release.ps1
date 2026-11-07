# ==============================================================================
# VortexDownloader - Automated Release Packager
# ==============================================================================
param(
    [string]$Version = "1.1.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  VortexDownloader v$Version Release Packager" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Run Tests First
Write-Host "`n[1/6] Running Unit Test Suite..." -ForegroundColor Yellow
Push-Location $RepoRoot
try {
    dotnet test tests/VortexDownloader.Tests/VortexDownloader.Tests.csproj -c $Configuration --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Unit tests failed. Aborting release package creation."
    }
    Write-Host "  [OK] All unit tests passed successfully." -ForegroundColor Green
}
finally {
    Pop-Location
}

# 2. Setup Output Directories
$DistDir = Join-Path $RepoRoot "dist"
$StagingDir = Join-Path $DistDir "staging"
$ReleasesDir = Join-Path $RepoRoot "releases"

Write-Host "`n[2/6] Preparing staging directory: $StagingDir" -ForegroundColor Yellow
if (Test-Path $DistDir) {
    Remove-Item -Recurse -Force $DistDir
}
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null
}

# 3. Publish Single-File Executable
Write-Host "`n[3/6] Publishing self-contained single-file executable for $Runtime..." -ForegroundColor Yellow
$UiProject = Join-Path $RepoRoot "src\VortexDownloader.UI\VortexDownloader.UI.csproj"
dotnet publish $UiProject -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $StagingDir

# Clean unnecessary pdb/json if generated
Get-ChildItem -Path $StagingDir -Include *.pdb,*.deps.json -File | Remove-Item -Force -ErrorAction SilentlyContinue

# Verify VortexDownloader.exe exists
$ExePath = Join-Path $StagingDir "VortexDownloader.exe"
if (-not (Test-Path $ExePath)) {
    $AltExe = Join-Path $StagingDir "VortexDownloader.UI.exe"
    if (Test-Path $AltExe) {
        Rename-Item -Path $AltExe -NewName "VortexDownloader.exe"
    } else {
        throw "Build failed: VortexDownloader.exe was not found in $StagingDir"
    }
}
Write-Host "  [OK] Standalone executable produced: VortexDownloader.exe" -ForegroundColor Green

# 4. Copy Browser Extension and Documentation
Write-Host "`n[4/6] Copying extension and user guides to staging..." -ForegroundColor Yellow
$ExtSource = Join-Path $RepoRoot "extension"
$ExtDest = Join-Path $StagingDir "extension"
Copy-Item -Recurse -Force -Path $ExtSource -Destination $ExtDest

# Copy Guides & Scripts
Copy-Item -Force -Path (Join-Path $RepoRoot "scripts\assets\Tarayici-Eklentisi-Kur.bat") -Destination (Join-Path $StagingDir "Tarayici-Eklentisi-Kur.bat")
Copy-Item -Force -Path (Join-Path $RepoRoot "scripts\assets\HIZLI_KURULUM.txt") -Destination (Join-Path $StagingDir "HIZLI_KURULUM.txt")
Copy-Item -Force -Path (Join-Path $RepoRoot "scripts\assets\QUICK_START.txt") -Destination (Join-Path $StagingDir "QUICK_START.txt")
Copy-Item -Force -Path (Join-Path $RepoRoot "LICENSE") -Destination (Join-Path $StagingDir "LICENSE.txt")

Write-Host "  [OK] Extension, guides, and helper scripts packaged." -ForegroundColor Green

# 5. Compress into ZIP Archive
$ZipFileName = "VortexDownloader-v$Version-$Runtime.zip"
$ZipFilePath = Join-Path $ReleasesDir $ZipFileName
if (Test-Path $ZipFilePath) {
    Remove-Item -Force $ZipFilePath
}

Write-Host "`n[5/6] Creating release ZIP archive: $ZipFileName..." -ForegroundColor Yellow
Compress-Archive -Path "$StagingDir\*" -DestinationPath $ZipFilePath -CompressionLevel Optimal
Write-Host "  [OK] Release ZIP created: $ZipFilePath" -ForegroundColor Green

# 6. Generate SHA-256 Checksum
Write-Host "`n[6/6] Generating SHA-256 hash..." -ForegroundColor Yellow
$Hash = (Get-FileHash -Path $ZipFilePath -Algorithm SHA256).Hash
$HashFile = "$ZipFilePath.sha256"
"$Hash  $ZipFileName" | Out-File -FilePath $HashFile -Encoding ascii
Write-Host "  [OK] Checksum file created: $HashFile" -ForegroundColor Green

# Clean staging dir
Remove-Item -Recurse -Force $DistDir -ErrorAction SilentlyContinue

$ZipLen = (Get-Item $ZipFilePath).Length
$ZipSizeMB = [math]::Round(($ZipLen / 1048576), 2)
Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "  Release Package Ready!" -ForegroundColor Green
Write-Host "  Package: $ZipFilePath" -ForegroundColor Green
Write-Host "  Size:    $ZipSizeMB MB" -ForegroundColor Green
Write-Host "  SHA256:  $Hash" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
