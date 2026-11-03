function Get-UploaderBuildEnvironment {
    param([string]$ProjectDirectory, [string]$DotnetRoot, [string]$MSBuildPath)

    $ErrorActionPreference = "Stop"
    if (-not $DotnetRoot) {
        $candidates = @(
            (Join-Path $ProjectDirectory "../dotnet8"),
            (Join-Path $env:ProgramFiles "dotnet"),
            (Join-Path $env:LOCALAPPDATA "Microsoft/dotnet")
        )
        foreach ($candidate in $candidates) {
            if ((Test-Path (Join-Path $candidate "dotnet.exe")) -and
                (Get-ChildItem (Join-Path $candidate "sdk") -Filter "8.*" -ErrorAction SilentlyContinue)) {
                $DotnetRoot = (Resolve-Path $candidate).Path
                break
            }
        }
    }
    if (-not $DotnetRoot -or -not (Test-Path (Join-Path $DotnetRoot "dotnet.exe")) -or
        -not (Get-ChildItem (Join-Path $DotnetRoot "sdk") -Filter "8.*" -ErrorAction SilentlyContinue)) {
        throw "找不到 .NET 8 SDK，请用 -DotnetRoot 指定包含 sdk/8.* 的 SDK 根目录。"
    }

    if (-not $MSBuildPath) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
        if (Test-Path -LiteralPath $vswhere) {
            $found = @(& $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
            if ($LASTEXITCODE -ne 0) { throw "vswhere 查找 MSBuild 失败。" }
            if ($found.Count -gt 0) { $MSBuildPath = $found[0] }
        }
    }
    if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) {
        throw "找不到 VS 2022 MSBuild。安装带 UWP/MSIX 组件的 VS Build Tools，或用 -MSBuildPath 指定 MSBuild.exe。"
    }
    foreach ($name in @("Steamworks.NET.dll", "steam_api64.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $ProjectDirectory "lib/$name"))) {
            throw "缺少 lib/$name，请按 CONTRIBUTING.md 和 lib/README.md 准备本地依赖。"
        }
    }
    [PSCustomObject]@{ DotnetRoot = $DotnetRoot; MSBuildPath = $MSBuildPath }
}
