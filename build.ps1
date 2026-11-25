# Builds Key Inject into an installable .streamDeckPlugin bundle.
# Requires only Windows built-ins: .NET Framework csc.exe + System.Drawing.
# Run from anywhere; paths are resolved relative to this file.

$ErrorActionPreference = "Stop"
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$src     = Join-Path $root "src\Bridge.cs"
$plugin  = Join-Path $root "plugin"
$assets  = Join-Path $plugin "assets"
$winDir  = Join-Path $plugin "windows"
$csc     = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$exe     = Join-Path $winDir "keyinject-bridge.exe"
$out     = Join-Path $root "KeyInject.streamDeckPlugin"

New-Item -ItemType Directory -Force -Path $assets, $winDir | Out-Null

# ---------------------------------------------------------------- compile
Write-Host "Compiling bridge..."
& $csc /nologo /target:exe /platform:x64 /optimize+ /reference:System.Drawing.dll /out:$exe $src
if ($LASTEXITCODE -ne 0) { throw "csc failed with $LASTEXITCODE" }

# ---------------------------------------------------------------- icons
Add-Type -AssemblyName System.Drawing

function New-Icon([string]$path, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::FromArgb(24, 24, 28))

    $s     = $size                        # shorthand
    $pad   = [Math]::Max(1, [int]($s * 0.10))
    $radius = [Math]::Max(2, [int]($s * 0.14))

    # rounded backdrop
    $back = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bw = $s - 2 * $pad
    $back.AddArc($pad, $pad, $radius * 2, $radius * 2, 180, 90)
    $back.AddArc($pad + $bw - $radius*2, $pad, $radius*2, $radius*2, 270, 90)
    $back.AddArc($pad + $bw - $radius*2, $pad + $bw - $radius*2, $radius*2, $radius*2, 0, 90)
    $back.AddArc($pad, $pad + $bw - $radius*2, $radius*2, $radius*2, 90, 90)
    $back.CloseFigure()
    $backBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 34, 37, 44))
    $g.FillPath($backBrush, $back)

    # keycap rows: 3 rows, highlighted bottom-right accent key
    $capBrush   = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 235, 235))
    $accBrush   = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 217, 140, 31))
    $kw = [Math]::Max(2, [int]($s * 0.16))
    $kh = [Math]::Max(2, [int]($s * 0.13))
    $gx = [Math]::Max(1, [int]($s * 0.045))
    $gy = [Math]::Max(1, [int]($s * 0.05))
    $top = [int]($s * 0.24)
    for ($row = 0; $row -lt 3; $row++) {
        $cols = if ($row -eq 2) { 2 } else { 3 }
        $rowW = $cols * $kw + ($cols - 1) * $gx
        $x0 = [int](($s - $rowW) / 2)
        for ($c = 0; $c -lt $cols; $c++) {
            $x = $x0 + $c * ($kw + $gx)
            $y = $top + $row * ($kh + $gy)
            $isAccent = ($row -eq 2 -and $c -eq $cols - 1)
            $g.FillRectangle($(if ($isAccent) { $accBrush } else { $capBrush }), $x, $y, $kw, $kh)
        }
    }

    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  icon: $path"
}

New-Icon (Join-Path $assets "action.png") 72
New-Icon (Join-Path $assets "plugin.png") 96
New-Icon (Join-Path $assets "bridge.png") 28

# ---------------------------------------------------------------- package
# Stream Deck requires the payload inside a top-level "<UUID>.sdPlugin/"
# folder, manifest.json first.
if (Test-Path $out) { Remove-Item $out -Force }
$uuid = (Get-Content (Join-Path $plugin "manifest.json") -Raw | ConvertFrom-Json).UUID
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs = [IO.File]::Open($out, [IO.FileMode]::Create)
$za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
$prefix = "$uuid.sdPlugin/"
$names = Get-ChildItem $plugin -Recurse -File | ForEach-Object { $_.FullName.Substring($plugin.Length + 1) -replace '\\', '/' }
foreach ($name in (@("manifest.json") + ($names | Where-Object { $_ -ne "manifest.json" } | Sort-Object))) {
    $entry = $za.CreateEntry($prefix + $name, [IO.Compression.CompressionLevel]::Optimal)
    $src = Join-Path $plugin ($name -replace '/', '\')
    $es = $entry.Open(); $bytes = [IO.File]::ReadAllBytes($src); $es.Write($bytes, 0, $bytes.Length); $es.Dispose()
}
$za.Dispose(); $fs.Dispose()

Write-Host ""
Write-Host "Bundle: $out"
Write-Host "Double-click it to install into Stream Deck (enable 'Allow plugins from community' if prompted)."
