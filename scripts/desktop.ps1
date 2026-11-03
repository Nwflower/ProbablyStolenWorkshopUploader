param([Parameter(ValueFromRemainingArguments = $true)][string[]]$TauriArguments)
. (Join-Path $PSScriptRoot 'env.ps1')
Push-Location $projectRoot
try {
    & npm.cmd run tauri -- @TauriArguments
    $result = $LASTEXITCODE
} finally { Pop-Location }
exit $result
