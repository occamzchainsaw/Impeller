#Requires -Version 7
<#
.SYNOPSIS
    Builds Impeller for handing to someone: two folders, each one a complete app.

.DESCRIPTION
    Release, self-contained, unpackaged. The engine and the shell each publish into their own
    folder because both carry their own copy of the .NET runtime and their file names collide;
    they are also installed to different places and by different people, so keeping them apart is
    honest rather than merely necessary.

    Trimming stays off for both. WinUI resolves types by name from XAML, the hardware layer
    reflects over LibreHardwareMonitor, and StreamJsonRpc builds its proxies at runtime. A trimmed
    build fails on navigation or in the tick loop, in Release only, on somebody else's machine.

.PARAMETER RuntimeIdentifier
    Defaults to this machine's architecture. Pass win-arm64 to cross-publish.

.PARAMETER Zip
    Also produce a .zip beside each folder, which is the thing actually worth sending.

.EXAMPLE
    .\scripts\publish.ps1 -Zip
#>
param(
    [string] $RuntimeIdentifier = "win-$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant())",
    [switch] $Zip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Asked of the build rather than parsed out of Directory.Build.props, so the folder name can never
# disagree with the version stamped into the assemblies.
$version = (dotnet msbuild (Join-Path $root 'src/Impeller.EngineService/Impeller.EngineService.csproj') `
    -getProperty:Version -p:Configuration=Release -v:q).Trim()

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) { throw 'could not read the version' }

$stage = Join-Path $root "artifacts/publish/$version"

# From scratch every time. A publish folder is not incremental - a file that a previous build
# produced and this one does not still ships, and finding that out is somebody else's crash.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Required lists the files whose absence is silent. Impeller.pri is there because publishing to a
# folder used to drop it: the app started, got as far as its first window, and died inside
# LoadComponent with nothing in the Event Log but Microsoft.UI.Xaml.dll and 0xc000027b. A missing
# file that produces a stowed WinRT exception is worth a line of script.
$apps = @(
    @{ Name = 'Impeller-Engine'; Project = 'src/Impeller.EngineService/Impeller.EngineService.csproj'; Required = @('Impeller.EngineService.exe', 'appsettings.json') }
    @{ Name = 'Impeller';        Project = 'src/Impeller.App.Shell/Impeller.App.Shell.csproj';         Required = @('Impeller.exe', 'Impeller.pri', 'Microsoft.UI.Xaml.dll', 'Assets/AppIcon.ico') }
)

foreach ($app in $apps) {
    $out = Join-Path $stage "$($app.Name)-$version-$RuntimeIdentifier"

    Write-Host "Publishing $($app.Name)..." -ForegroundColor Cyan

    dotnet publish (Join-Path $root $app.Project) `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishTrimmed=false `
        -o $out `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "publish failed: $($app.Name)" }

    $missing = $app.Required | Where-Object { -not (Test-Path (Join-Path $out $_)) }
    if ($missing) { throw "missing from $out : $($missing -join ', ')" }

    $exe = Join-Path $out $app.Required[0]
    $size = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host ("  {0}  ({1} MB)" -f $exe, $size) -ForegroundColor DarkGray

    if ($Zip) {
        $archive = "$out.zip"
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive -Force
        Write-Host "  $archive" -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host "Published $version to $stage" -ForegroundColor Green
Write-Host 'Install: docs/installing.md' -ForegroundColor DarkGray
