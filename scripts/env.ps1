$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localCargo = Join-Path $projectRoot '.toolchains/cargo'
if (Test-Path -LiteralPath (Join-Path $localCargo 'bin/cargo.exe')) {
    $env:CARGO_HOME = $localCargo
    $env:RUSTUP_HOME = Join-Path $projectRoot '.toolchains/rustup'
    $env:PATH = (Join-Path $localCargo 'bin') + [IO.Path]::PathSeparator + $env:PATH
}
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'Rust is missing. Install the toolchain described in CONTRIBUTING.md.'
}
