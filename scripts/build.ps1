<#
功能：编译 ClipImageToPath 项目
参数:
    -Configuration: 构建配置，默认 Release
    -Clean: 先清理再编译
返回:
    int，0=成功，1=失败
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Clean
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

# 前置检查：SDK 缺失时给出明确提示，而不是让 dotnet 输出晦涩错误
$dotnetPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetPath) {
    Write-Host '错误：未找到 dotnet 命令，请先安装 .NET SDK 8.0+ 或运行 check-env.ps1' -ForegroundColor 'Red'
    Confirm-Exit 1
}

if ($Clean) {
    Write-Host "清理构建产物 ($Configuration) ..." -ForegroundColor 'Cyan'
    & dotnet clean $ProjectFile -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host '清理失败' -ForegroundColor 'Red'
        Confirm-Exit 1
    }
}

Write-Host "开始编译 ($Configuration) ..." -ForegroundColor 'Cyan'
& dotnet build $ProjectFile -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host '编译失败' -ForegroundColor 'Red'
    Confirm-Exit 1
}

# 中间产物统一输出到 build/（见 Directory.Build.props 的 BaseOutputPath 配置）
$outputDir = Join-Path $ProjectRoot "build\bin\$Configuration\net8.0-windows"
Write-Host ("编译成功：{0}" -f $outputDir) -ForegroundColor 'Green'
Confirm-Exit 0
