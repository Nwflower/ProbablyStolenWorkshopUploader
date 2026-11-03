param(
    [string]$OutputDirectory = "",
    [string]$DotnetRoot = "",
    [string]$MSBuildPath = "",
    [string]$RestoreSources = ""
)

# 构建一个中文资源版 ZIP。保留程序运行所需的目录结构，避免把历史 bin/obj 打进包。
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "dist" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

. (Join-Path $PSScriptRoot "packaging/BuildEnvironment.ps1")
$buildEnvironment = Get-UploaderBuildEnvironment $PSScriptRoot $DotnetRoot $MSBuildPath
$DotnetRoot = $buildEnvironment.DotnetRoot
$msbuild = $buildEnvironment.MSBuildPath
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) { throw "找不到 Windows .NET Framework C# 编译器：$csc" }

Push-Location $repoRoot
try {
    $id = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $work = Join-Path $repoRoot ("scratch/workshop-uploader-build-" + $id)
    $buildBin = (Join-Path $work "bin") + [IO.Path]::DirectorySeparatorChar
    $buildObj = (Join-Path $work "obj") + [IO.Path]::DirectorySeparatorChar
    New-Item -ItemType Directory -Path $work | Out-Null
    $env:DOTNET_ROOT = $DotnetRoot
    $arguments = @(
        (Join-Path $PSScriptRoot "WorkshopUploader.csproj"),
        "-restore", "-p:Configuration=Release", "-p:Platform=x64",
        "-p:WindowsAppSDKSelfContained=true", "-p:SelfContained=false",
        "-p:BaseOutputPath=$buildBin", "-p:BaseIntermediateOutputPath=$buildObj",
        "-nologo", "-v:m"
    )
    if ($RestoreSources) {
        $arguments += "-p:RestoreSources=$RestoreSources"
        $arguments += "-p:NuGetAudit=false"
    }
    & $msbuild @arguments
    if ($LASTEXITCODE -ne 0) { throw "上传器编译失败。" }

    $buildOutput = Join-Path $buildBin "x64/Release/net8.0-windows10.0.19041.0/win-x64"
    if (-not (Test-Path -LiteralPath (Join-Path $buildOutput "ProbablyStolenWorkshopUploader.exe"))) {
        throw "找不到上传器构建产物：$buildOutput"
    }

    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName(
        (Join-Path $buildOutput "ProbablyStolenWorkshopUploader.dll")).Version
    $productVersion = "{0}.{1}.{2}" -f $assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build
    $name = "ProbablyStolenWorkshopUploader-v{0}-win-x64-zh-{1}" -f $productVersion, $id
    $stage = Join-Path $OutputDirectory $name
    $app = Join-Path $stage "app"
    $docs = Join-Path $stage "docs"
    New-Item -ItemType Directory -Path $app, $docs -Force | Out-Null

    foreach ($item in Get-ChildItem -LiteralPath $buildOutput -Force) {
        if ($item.PSIsContainer) {
            if ($item.Name -in @("bin", "obj")) { continue }
            # WinUI 的独立 MUI 目录只留简体、繁体中文；其余依赖目录原样保留。
            if ($item.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})+$' -and
                $item.Name -notin @("zh-CN", "zh-TW")) { continue }
            Copy-Item -LiteralPath $item.FullName -Destination $app -Recurse
        }
        elseif ($item.Extension -notin @(".pdb", ".log")) {
            Copy-Item -LiteralPath $item.FullName -Destination $app
        }
    }

    $launcher = Join-Path $stage "ProbablyStolenWorkshopUploader.exe"
    $icon = Join-Path $PSScriptRoot "Assets/probably-stolen.ico"
    $versionSource = Join-Path $work "LauncherVersion.cs"
    $versionAttributes = @"
using System.Reflection;
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$productVersion")]
"@
    Set-Content -LiteralPath $versionSource -Value $versionAttributes -Encoding UTF8
    & $csc /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll `
        "/win32icon:$icon" "/out:$launcher" (Join-Path $PSScriptRoot "packaging/Launcher.cs") $versionSource
    if ($LASTEXITCODE -ne 0) { throw "启动器编译失败。" }

    & (Join-Path $PSScriptRoot "export-source.ps1") -OutputDirectory (Join-Path $docs "Source") -NoZip
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "LICENSE") -Destination (Join-Path $docs "LICENSE.txt")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.md") -Destination $docs
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "licenses") -Destination $docs -Recurse

    $readme = @'
Probably Stolen 创意工坊上传器

1. 把压缩包全部解压到同一文件夹，双击根目录的 ProbablyStolenWorkshopUploader.exe。
2. 需要 64 位 Windows 10 1809 或更新版本，以及 .NET 8 Desktop Runtime x64：
   https://dotnet.microsoft.com/en-us/download/dotnet/8.0
   Windows App SDK 运行时已经包含在 app 文件夹中。
3. 先启动并登录 Steam，使用拥有 Probably Stolen 的账号。物品发布到当前登录账号名下。
4. 左侧扫描游戏 Mods 文件夹，也可以用“选择文件夹…”选择含 manifest.xml 的模组包。
5. 核对发布目标、模组路径、版本和更新说明后，再提交。
6. 如果提示“提交结果未确认”，先检查工坊页面和更新记录，再决定是否重试。
7. 首次启动会展示 QQ 群信息；以后可在顶部“关于”中查看作者和群号。

app 是程序运行目录，请保留整个文件夹。源码与许可证在 docs 文件夹中。
本地设置保存在 %APPDATA%\ProbablyStolenWorkshopUploader，与程序文件分开。
'@
    Set-Content -LiteralPath (Join-Path $stage "使用说明.txt") -Value $readme -Encoding UTF8

    $zip = Join-Path $OutputDirectory ($name + ".zip")
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "发布目录：$stage"
    Write-Host "压缩包：$zip"
}
finally {
    Pop-Location
}
