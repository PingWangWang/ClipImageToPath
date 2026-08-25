<#
功能：检查 ClipImageToPath 项目的构建环境是否就绪
参数:
    -CheckNetwork: 可选开关，联网验证 NuGet 源可达性
返回:
    int，0=全部通过，1=存在不满足项
#>
param(
    [switch]$CheckNetwork
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

# 项目根目录：脚本固定位于 <项目根>/scripts
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'src\ClipImageToPath.csproj'

$failCount = 0

function Write-Result {
    param(
        [string]$Status,
        [string]$Message
    )
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        default { 'Yellow' }
    }
    Write-Host ("[{0}] {1}" -f $Status, $Message) -ForegroundColor $color
}

# --- 1. PowerShell 版本：脚本语法与语义依赖 5.1+ ---
$psVersion = $PSVersionTable.PSVersion
if ($psVersion.Major -ge 5) {
    Write-Result 'PASS' ("PowerShell 版本 {0}" -f $psVersion)
}
else {
    Write-Result 'FAIL' ("PowerShell 版本过低：{0}，需要 5.1+" -f $psVersion)
    $failCount++
}

# --- 2. Windows 平台：目标框架 net8.0-windows 依赖 Windows ---
$isWindowsPlatform = ($env:OS -eq 'Windows_NT')
if ($isWindowsPlatform) {
    Write-Result 'PASS' 'Windows 平台'
}
else {
    Write-Result 'FAIL' '非 Windows 平台，目标框架 net8.0-windows 无法构建'
    $failCount++
}

# --- 3. .NET SDK：要求 8.0 及以上（项目目标框架为 net8.0-windows）---
$dotnetPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetPath) {
    Write-Result 'FAIL' '未找到 dotnet 命令，请安装 .NET SDK 8.0+：https://dotnet.microsoft.com/download'
    $failCount++
}
else {
    $dotnetVersion = (& dotnet --version 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetVersion)) {
        Write-Result 'FAIL' 'dotnet 命令存在但无法获取版本'
        $failCount++
    }
    else {
        $major = [int]($dotnetVersion.Split('.')[0])
        if ($major -ge 8) {
            Write-Result 'PASS' ('.NET SDK 版本 {0}' -f $dotnetVersion)
        }
        else {
            Write-Result 'FAIL' ('.NET SDK 版本过低：{0}，需要 8.0+' -f $dotnetVersion)
            $failCount++
        }
    }
}

# --- 4. 项目文件存在性 ---
if (Test-Path -LiteralPath $ProjectFile) {
    Write-Result 'PASS' ("项目文件：{0}" -f $ProjectFile)
}
else {
    Write-Result 'FAIL' ("项目文件缺失：{0}" -f $ProjectFile)
    $failCount++
}

# --- 5. NuGet 源可达性（可选）：构建还原阶段需要联网拉取包 ---
if ($CheckNetwork) {
    try {
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(10)
        $response = $client.GetAsync('https://api.nuget.org/v3/index.json').GetAwaiter().GetResult()
        if ($response.IsSuccessStatusCode) {
            Write-Result 'PASS' 'NuGet 源可达'
        }
        else {
            Write-Result 'FAIL' ("NuGet 源返回异常状态码：{0}" -f $response.StatusCode)
            $failCount++
        }
        $client.Dispose()
    }
    catch {
        Write-Result 'FAIL' ("NuGet 源不可达：{0}" -f $_.Exception.Message)
        $failCount++
    }
}

Write-Host ''
if ($failCount -eq 0) {
    Write-Host '环境检查通过：可以执行 build.ps1 与 publish.ps1' -ForegroundColor 'Green'
    Confirm-Exit 0
}
else {
    Write-Host ("环境检查未通过：{0} 项不满足" -f $failCount) -ForegroundColor 'Red'
    Confirm-Exit 1
}
