param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "dist")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$iconPath = Join-Path $PSScriptRoot "LiceAIDashboard.ico"
$bitmap = New-Object Drawing.Bitmap 64,64
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$rect = New-Object Drawing.Rectangle 2,2,60,60
$brush = New-Object Drawing.Drawing2D.LinearGradientBrush $rect,([Drawing.Color]::FromArgb(124,140,255)),([Drawing.Color]::FromArgb(52,211,153)),45
$graphics.FillEllipse($brush,$rect)
$pen = New-Object Drawing.Pen ([Drawing.Color]::White),6
$pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
$graphics.DrawArc($pen,16,15,32,32,205,235)
$graphics.DrawLine($pen,32,32,44,24)
$graphics.FillEllipse([Drawing.Brushes]::White,27,27,10,10)
$pngStream = New-Object IO.MemoryStream
$bitmap.Save($pngStream,[Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()
$pngStream.Dispose()
$stream = [IO.File]::Create($iconPath)
$writer = New-Object IO.BinaryWriter $stream
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]64)
$writer.Write([byte]64)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$pngBytes.Length)
$writer.Write([uint32]22)
$writer.Write($pngBytes)
$writer.Flush()
$writer.Dispose()
$graphics.Dispose()
$brush.Dispose()
$pen.Dispose()
$bitmap.Dispose()

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
$output = Join-Path $OutputDirectory "Lice AI Dashboard.exe"
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /win32icon:"$iconPath" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /out:"$output" (Join-Path $PSScriptRoot "LiceAIDashboard.cs")
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed." }
Get-Item -LiteralPath $output
