param([string[]]$Names = @('impeller','rigfan'), [int[]]$Sizes = @(16,20,24,32,40,48,64,128,256))

Add-Type -AssemblyName System.Drawing

$dir = $PSScriptRoot
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$uri = ($dir -replace '\\', '/')

function IsBlank([string]$path) {
    if (-not (Test-Path $path)) { return $true }
    $b = [System.Drawing.Bitmap]::FromFile($path)
    try {
        for ($y = 0; $y -lt $b.Height; $y += 2) {
            for ($x = 0; $x -lt $b.Width; $x += 2) {
                if ($b.GetPixel($x, $y).A -gt 8) { return $false }
            }
        }
        return $true
    }
    finally { $b.Dispose() }
}

function Render([string]$name, [int]$size, [int]$window, [int]$scale) {
    $out = Join-Path $dir "$name-$size.png"
    if (Test-Path $out) { Remove-Item $out -Force }

    # A profile per render. Sharing one makes the second instance defer to the first, which then
    # never writes its screenshot.
    $udd = Join-Path $env:TEMP ("edge-icon-" + [guid]::NewGuid().ToString("N"))

    $arguments = @(
        '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
        '--hide-scrollbars', '--virtual-time-budget=2000',
        "--user-data-dir=$udd", '--default-background-color=00000000',
        "--force-device-scale-factor=$scale", "--window-size=$window,$window",
        "--screenshot=$out", "file:///$uri/$name.svg"
    )

    $p = Start-Process -FilePath $edge -ArgumentList $arguments -NoNewWindow -PassThru
    if (-not $p.WaitForExit(30000)) { $p.Kill() }
    Remove-Item $udd -Recurse -Force -ErrorAction SilentlyContinue
}

foreach ($name in $Names) {
    $report = "$name :"

    foreach ($size in $Sizes) {
        Render $name $size $size 1

        # 128 comes back fully transparent from this renderer, every time, at that size alone.
        # Asking for half the window at twice the device scale produces the same pixel count from
        # the same vector and does not. The check is on every size rather than special-casing the
        # one that is known to fail, because a silently blank frame is exactly the defect that ships.
        if ((IsBlank (Join-Path $dir "$name-$size.png")) -and ($size % 2 -eq 0)) {
            Render $name $size ($size / 2) 2
        }

        $path = Join-Path $dir "$name-$size.png"
        if (IsBlank $path) { $report += " $size(BLANK)" }
        else {
            $img = [System.Drawing.Image]::FromFile($path)
            $report += " $size($($img.Width))"
            $img.Dispose()
        }
    }

    $report
}
