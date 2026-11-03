. (Join-Path $PSScriptRoot 'env.ps1')
Push-Location $projectRoot
try {
    & cargo fmt --all -- --check
    if ($LASTEXITCODE -ne 0) { throw 'cargo fmt failed' }
    & cargo test -p uploader-core --locked
    if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed' }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
    & npm.cmd test
    if ($LASTEXITCODE -ne 0) { throw 'Frontend tests failed' }
} finally { Pop-Location }
