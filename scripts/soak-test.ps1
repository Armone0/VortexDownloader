# Runs the engine soak test (300 local downloads, some cancelled) and prints before/after
# managed-memory and handle numbers. Needs no network: everything talks to loopback servers.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:VORTEX_SOAK = '1'
try {
    dotnet test (Join-Path $root 'tests/VortexDownloader.Tests') --filter 'Category=Soak' `
        --logger 'console;verbosity=detailed' | Tee-Object -Variable out | Select-String 'SOAK|Passed|Failed|Başarılı|Başarısız'
    if ($LASTEXITCODE -ne 0) { Write-Error 'Soak test failed'; exit 1 }
} finally {
    Remove-Item Env:VORTEX_SOAK -ErrorAction SilentlyContinue
}
