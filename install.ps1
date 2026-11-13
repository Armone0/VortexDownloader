# One-line install:  irm https://raw.githubusercontent.com/Armone0/VortexDownloader/main/install.ps1 | iex
# Downloads the latest release, checks its SHA-256, extracts it to %LOCALAPPDATA%\Programs\VortexDownloader
# and adds a Start menu shortcut. Run it again to update; settings live in AppData and are kept.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$release = Invoke-RestMethod 'https://api.github.com/repos/Armone0/VortexDownloader/releases/latest' -Headers @{ 'User-Agent' = 'VortexDownloader-installer' }
$zip = $release.assets | Where-Object name -like '*-win-x64.zip' | Select-Object -First 1
$sum = $release.assets | Where-Object name -eq "$($zip.name).sha256" | Select-Object -First 1
if (-not $zip -or -not $sum) { throw "Release $($release.tag_name) has no win-x64 zip with a .sha256 file." }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("vortex-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
    $zipPath = Join-Path $tmp $zip.name
    Write-Host "Downloading $($zip.name)..."
    Invoke-WebRequest $zip.browser_download_url -OutFile $zipPath -UseBasicParsing
    # WebClient, because PS 5.1's Invoke-WebRequest hands back bytes for an octet-stream file
    $expected = ((New-Object Net.WebClient).DownloadString($sum.browser_download_url).Trim() -split '\s+')[0]
    $actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual. Nothing was installed." }

    $dest = Join-Path $env:LOCALAPPDATA 'Programs\VortexDownloader'
    Get-Process VortexDownloader -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force
    Expand-Archive $zipPath $dest -Force

    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'VortexDownloader.lnk'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $shortcut.TargetPath = Join-Path $dest 'VortexDownloader.exe'
    $shortcut.WorkingDirectory = $dest
    $shortcut.Save()

    Write-Host "VortexDownloader $($release.tag_name) installed to $dest (SHA-256 verified)." -ForegroundColor Green
    Write-Host "Start it from the Start menu, or run: & '$($shortcut.TargetPath)'"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
