<#
功能：打包发布 ClipImageToPath 为单文件自包含 exe
参数:
    -Configuration: 发布配置，默认 Release
    -Runtime: 目标运行时标识，默认 win-x64
    -Version: 版本号，默认读取 csproj 的 <Version>；同时决定 exe 文件名与文件属性版本
    -Output: 输出目录，默认 <项目根>\artifacts\publish
返回:
    int，0=成功，1=失败
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [string]$Output
)

$ErrorActionPreference = 'Stop'

# [修改] 交互式控制台（手动运行/双击）时暂停等待确认，避免窗口一闪而过看不到结果；
#       输出被重定向（CI/管道）时自动跳过，避免自动化挂起
function Confirm-Exit {
    param(
        [int]$Code = 0
    )
    if ([Environment]::UserInteractive -and -not [Console]::IsOutputRedirected) {
        Write-Host ''
        Read-Host '脚本执行完成，按回车键退出'
    }
    exit $Code
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'src\ClipImageToPath.csproj'
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $ProjectRoot 'artifacts\publish'
}

# 版本号默认取 csproj 中的 <Version>，保证文件名、文件属性与源码版本单一来源一致
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath $ProjectFile -Raw
    $Version = (($project.Project.PropertyGroup | Where-Object { $_.Version }).Version | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($Version)) {
        Write-Host '错误：无法从 csproj 解析 Version，请在 csproj 中设置 <Version>' -ForegroundColor 'Red'
        Confirm-Exit 1
    }
}

# 前置检查：SDK 缺失时给出明确提示，而不是让 dotnet 输出晦涩错误
$dotnetPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetPath) {
    Write-Host '错误：未找到 dotnet 命令，请先安装 .NET SDK 8.0+ 或运行 check-env.ps1' -ForegroundColor 'Red'
    Confirm-Exit 1
}

# 发布目录只保留本次产物，避免旧版本 exe 残留造成混淆
if (Test-Path -LiteralPath $Output) {
    Write-Host ("清理旧发布产物：{0}" -f $Output) -ForegroundColor 'Cyan'
    Get-ChildItem -LiteralPath $Output -Force | Remove-Item -Recurse -Force
}
else {
    [System.IO.Directory]::CreateDirectory($Output) | Out-Null
}

Write-Host "开始发布 ($Configuration / $Runtime / v$Version) ..." -ForegroundColor 'Cyan'
& dotnet publish $ProjectFile `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -o $Output `
    --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host '发布失败' -ForegroundColor 'Red'
    exit 1
}

# 将 exe 重命名为带版本号的文件名（如 ClipImageToPath_1.0.0.exe）
$plainExe = Join-Path $Output 'ClipImageToPath.exe'
$versionedExe = Join-Path $Output ("ClipImageToPath_{0}.exe" -f $Version)
if (-not (Test-Path -LiteralPath $plainExe)) {
    Write-Host ("发布产物缺失：{0}" -f $plainExe) -ForegroundColor 'Red'
    Confirm-Exit 1
}
$plainPdb = Join-Path $Output 'ClipImageToPath.pdb'
if (Test-Path -LiteralPath $plainPdb) {
    Move-Item -LiteralPath $plainPdb -Destination (Join-Path $Output ("ClipImageToPath_{0}.pdb" -f $Version)) -Force
}
Move-Item -LiteralPath $plainExe -Destination $versionedExe -Force

$sizeMB = [math]::Round((Get-Item -LiteralPath $versionedExe).Length / 1MB, 2)
$hash = (Get-FileHash -LiteralPath $versionedExe -Algorithm SHA256).Hash
$fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($versionedExe)
Write-Host ("发布成功：{0}（{1} MB）" -f $versionedExe, $sizeMB) -ForegroundColor 'Green'
Write-Host ("文件属性版本：产品 {0} / 文件 {1}" -f $fileInfo.ProductVersion, $fileInfo.FileVersion) -ForegroundColor 'Green'
Write-Host ("SHA256：{0}" -f $hash) -ForegroundColor 'Green'
Confirm-Exit 0
