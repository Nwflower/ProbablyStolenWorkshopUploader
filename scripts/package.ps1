param([string]$OutputDirectory = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'release/portable' }
. (Join-Path $PSScriptRoot 'env.ps1')
Push-Location $root
try {
    & npm.cmd run tauri -- build --no-bundle
    if ($LASTEXITCODE -ne 0) { throw 'tauri build failed' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $exe = Join-Path $root 'target/release/workshop-uploader.exe'
    Copy-Item -LiteralPath $exe -Destination $OutputDirectory -Force
    Copy-Item -LiteralPath (Join-Path $root 'steam/steam_api64.dll') -Destination $OutputDirectory -Force
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'steam_appid.txt') -Value '4348910' -Encoding ascii
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $OutputDirectory -Force
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $OutputDirectory -Force
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory -Force
    Write-Host "portable build: $OutputDirectory"
} finally { Pop-Location }
