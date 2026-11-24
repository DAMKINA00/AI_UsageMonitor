# Copies ChatterBox's character expression art (5 characters x 12 expressions) into Assets/Faces,
# downscaled so the monitor stays small. Run from the repository root:
#   pwsh art/import_chatterbox_faces.ps1 -Source ..\AI_ChatterBox\src\renderer\public\characters
param(
    [string]$Source = (Join-Path $PSScriptRoot "..\..\AI_ChatterBox\src\renderer\public\characters"),
    [int]$Size = 256
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$characters = "orch", "claude", "gpt", "gemini", "grok"
$expressions = "neutral", "thinking", "speaking", "happy", "confident", "surprised",
    "embarrassed", "worried", "sad", "angry", "tired", "waiting"
$target = Join-Path $PSScriptRoot "..\Assets\Faces"

foreach ($character in $characters) {
    $dir = Join-Path $target $character
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($expression in $expressions) {
        $from = Join-Path $Source "$character\$expression.png"
        if (-not (Test-Path $from)) { throw "missing $from" }
        $image = [System.Drawing.Image]::FromFile((Resolve-Path $from))
        try {
            $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $attributes = New-Object System.Drawing.Imaging.ImageAttributes
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($image, (New-Object System.Drawing.Rectangle 0, 0, $Size, $Size), 0, 0, $image.Width, $image.Height,
                [System.Drawing.GraphicsUnit]::Pixel, $attributes)
            $graphics.Dispose()
            $bitmap.Save((Join-Path $dir "$expression.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            $bitmap.Dispose()
        }
        finally { $image.Dispose() }
    }
}
Write-Host "imported $($characters.Count * $expressions.Count) faces into $target"
