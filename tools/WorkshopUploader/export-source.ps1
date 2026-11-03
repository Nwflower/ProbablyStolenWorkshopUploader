param(
    [string]$OutputDirectory = "",
    [switch]$NoZip
)

# 白名单导出独立源码仓库；不复制工作区历史、账号数据或二进制依赖。
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot "WorkshopUploader.csproj") -Raw)
$version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $OutputDirectory) {
    $suffix = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $OutputDirectory = Join-Path $repoRoot "dist/ProbablyStolenWorkshopUploader-source-v$version-$suffix"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "拒绝覆盖已有目录：$OutputDirectory" }
if (-not $NoZip -and (Test-Path -LiteralPath ($OutputDirectory + ".zip"))) {
    throw "拒绝覆盖已有 ZIP：$OutputDirectory.zip"
}
$target = Join-Path $OutputDirectory "tools/WorkshopUploader"
$testTarget = Join-Path $OutputDirectory "tests/WorkshopUploaderTests"
New-Item -ItemType Directory -Path $target, $testTarget | Out-Null

$files = @(
    "App.xaml", "App.xaml.cs", "MainWindow.xaml", "MainWindow.xaml.cs",
    "ManifestReader.cs", "ModDiscovery.cs", "Program.cs", "SteamUploader.cs",
    "WorkshopMeta.cs", "WorkshopVdf.cs", "WorkshopUploader.csproj", "steam_appid.txt",
    "build.ps1", "package.ps1", "export-source.ps1", "README.md", "CONTRIBUTING.md",
    "LICENSE", "THIRD-PARTY-NOTICES.md",
    "Assets/change-preview.svg", "Assets/probably-stolen.ico", "Assets/probably-stolen.png",
    "lib/README.md", "licenses/Steamworks.NET-MIT.txt",
    "packaging/Launcher.cs", "packaging/BuildEnvironment.ps1",
    "packaging/README.github.md.in", "packaging/gitignore.txt", "packaging/offline-tests.yml"
)
foreach ($file in $files) {
    $destination = Join-Path $target $file
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $destination
}
foreach ($file in @("WorkshopUploaderTests.csproj", "Program.cs", "FakeSteam.cs")) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "tests/WorkshopUploaderTests/$file") -Destination $testTarget
}
foreach ($file in @("LICENSE", "CONTRIBUTING.md", "THIRD-PARTY-NOTICES.md")) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $OutputDirectory
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "licenses") -Destination $OutputDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "packaging/README.github.md.in") -Destination (Join-Path $OutputDirectory "README.md")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "packaging/gitignore.txt") -Destination (Join-Path $OutputDirectory ".gitignore")
$workflowDirectory = Join-Path $OutputDirectory ".github/workflows"
New-Item -ItemType Directory -Path $workflowDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "packaging/offline-tests.yml") -Destination $workflowDirectory
if (-not $NoZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($OutputDirectory, ($OutputDirectory + ".zip"), [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "源码 ZIP：$OutputDirectory.zip"
}
Write-Host "源码目录：$OutputDirectory"
