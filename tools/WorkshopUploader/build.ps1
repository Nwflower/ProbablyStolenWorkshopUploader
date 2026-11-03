param(
    [string]$Configuration = "Release",
    [string]$DotnetRoot = "",
    [string]$MSBuildPath = "",
    [string]$RestoreSources = "",
    # 默认框架依赖（约 37 MB）：要求目标机器装有 Windows App Runtime 1.7（含 DDLM）。
    # 缺运行时时 Windows App SDK 的引导会弹窗并提供下载链接。
    # 加 -SelfContained 会包含 Windows App SDK；目标机器仍需 .NET 8 运行时。
    [switch]$SelfContained
)

# WinUI 3 工程不能用 dotnet build：Windows App SDK 的构建目标需要 Visual Studio 提供的
# MSBuild 任务（Microsoft.Build.Packaging.Pri.Tasks / Microsoft.Build.AppxPackage）。
# 本脚本用 VS 的 MSBuild + .NET 8 SDK 构建。

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "packaging/BuildEnvironment.ps1")
$buildEnvironment = Get-UploaderBuildEnvironment $PSScriptRoot $DotnetRoot $MSBuildPath
$DotnetRoot = $buildEnvironment.DotnetRoot
$msbuild = $buildEnvironment.MSBuildPath

$env:DOTNET_ROOT = $DotnetRoot
Write-Host "DOTNET_ROOT = $DotnetRoot"
$sc = $SelfContained.IsPresent
Write-Host "WindowsAppSDKSelfContained = $sc"

$outDir = Join-Path $PSScriptRoot $(if ($sc) { "bin/sc/" } else { "bin/fd/" })

$arguments = @(
    (Join-Path $PSScriptRoot "WorkshopUploader.csproj"), "-restore",
    "-p:Configuration=$Configuration", "-p:Platform=x64",
    "-p:WindowsAppSDKSelfContained=$($sc.ToString().ToLowerInvariant())",
    "-p:BaseOutputPath=$outDir", "-nologo", "-v:m"
)
if ($RestoreSources) {
    $arguments += "-p:RestoreSources=$RestoreSources"
    $arguments += "-p:NuGetAudit=false"
}
& $msbuild @arguments
exit $LASTEXITCODE
