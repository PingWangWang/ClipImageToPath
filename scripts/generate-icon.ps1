<#
功能：生成 ClipImageToPath 的 Windows 图标（多尺寸 .ico，PNG 压缩格式）
参数:
    -OutputPath: 输出 ico 路径，默认 <项目根>\src\Assets\app.ico
返回:
    int，0=成功，1=失败
#>
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

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
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $ProjectRoot 'src\Assets\app.ico'
}

function New-RoundedRectPath {
    param(
        [float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius
    )
    $d = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-MasterBitmap {
    $size = 256
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # 背景：深蓝渐变圆角方块，作为图标主色
    $bgPath = New-RoundedRectPath 12 12 232 232 56
    $bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Point]::new(12, 12),
        [System.Drawing.Point]::new(244, 244),
        [System.Drawing.Color]::FromArgb(255, 46, 111, 238),
        [System.Drawing.Color]::FromArgb(255, 29, 78, 216))
    $g.FillPath($bgBrush, $bgPath)

    # 白色图片卡片：太阳与山形，表达"剪贴板中的图片"
    $cardPath = New-RoundedRectPath 68 44 120 112 28
    $g.FillPath([System.Drawing.Brushes]::White, $cardPath)
    $g.FillEllipse([System.Drawing.Brushes]::Gold, 98, 70, 26, 26)
    $mount = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $mount.AddPolygon([System.Drawing.Point[]]@(
        [System.Drawing.Point]::new(80, 144),
        [System.Drawing.Point]::new(126, 96),
        [System.Drawing.Point]::new(156, 128),
        [System.Drawing.Point]::new(172, 112),
        [System.Drawing.Point]::new(184, 144)))
    $mountBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 96, 165, 250))
    $g.FillPath($mountBrush, $mount)

    # 底部绿色路径条 + 白色路径文本线，表达"转为文件路径"
    $barPath = New-RoundedRectPath 68 172 120 32 16
    $barBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 34, 197, 94))
    $g.FillPath($barBrush, $barPath)
    $linePen = [System.Drawing.Pen]::new([System.Drawing.Brushes]::White, 8)
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($linePen, 86, 182, 174, 182)
    $g.DrawLine($linePen, 86, 196, 150, 196)

    $mount.Dispose(); $mountBrush.Dispose()
    $bgPath.Dispose(); $bgBrush.Dispose()
    $cardPath.Dispose(); $barPath.Dispose(); $barBrush.Dispose()
    $linePen.Dispose(); $g.Dispose()
    return $bmp
}

function New-IconFile {
    param(
        [System.Drawing.Bitmap]$Master,
        [int[]]$Sizes,
        [string]$Path
    )
    $entries = @()
    foreach ($s in $Sizes) {
        if ($s -eq 256) {
            $target = $Master
        }
        else {
            $target = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($target)
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.DrawImage($Master, 0, 0, $s, $s)
            $g.Dispose()
        }
        $ms = [System.IO.MemoryStream]::new()
        $target.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $entries += [PSCustomObject]@{ Size = $s; Data = $ms.ToArray() }
        $ms.Dispose()
        if ($s -ne 256) { $target.Dispose() }
    }

    # 手写 ICO 容器：ICONDIR + ICONDIRENTRY + PNG 数据（Vista+ 支持 PNG 压缩条目）
    $dir = Split-Path -Parent $Path
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    $fs = [System.IO.File]::Create($Path)
    $bw = [System.IO.BinaryWriter]::new($fs)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -eq 256) { [byte]0 } else { [byte]$e.Size }
        $bw.Write($dim); $bw.Write($dim)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$e.Data.Length)
        $bw.Write([uint32]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $bw.Write($e.Data) }
    $bw.Close(); $fs.Dispose()
}

$master = New-MasterBitmap
New-IconFile -Master $master -Sizes @(16, 32, 48, 256) -Path $OutputPath
$master.Dispose()
$info = Get-Item -LiteralPath $OutputPath
Write-Host ("图标已生成：{0}（{1} KB，16/32/48/256 尺寸）" -f $OutputPath, [math]::Round($info.Length / 1KB, 1)) -ForegroundColor 'Green'
Confirm-Exit 0
