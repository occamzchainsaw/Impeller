#Requires -Version 7
<#
.SYNOPSIS
    Builds Impeller-<version>-win-x64-Setup.exe: one file, engine and window both.

.DESCRIPTION
    Publishes both halves, then compiles installer\Impeller.iss over the result. The installer
    registers the engine as a Windows service and lets the user choose where it all goes, which is
    why it needs administrator to run — building it does not.

    Publishing here rather than trusting whatever is already in artifacts\publish: an installer
    built over a stale folder is the one mistake in this whole chain that nobody would notice until
    it was on somebody else's machine.

.PARAMETER SkipPublish
    Use the existing publish output. For iterating on the .iss file, where re-publishing half a
    gigabyte to change a message is a waste of two minutes.

.EXAMPLE
    .\scripts\pack-installer.ps1
#>
param(
    [string] $RuntimeIdentifier = "win-$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant())",
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Inno installs per-user by default through winget, and per-machine through its own installer. Both
# are ordinary, so both are looked for rather than one being declared correct.
$iscc = @(
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw 'Inno Setup 6 is not installed. Run: winget install JRSoftware.InnoSetup' }

$version = (dotnet msbuild (Join-Path $root 'src/Impeller.EngineService/Impeller.EngineService.csproj') `
    -getProperty:Version -p:Configuration=Release -v:q).Trim()

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) { throw 'could not read the version' }

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -RuntimeIdentifier $RuntimeIdentifier
    if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
}

$stage = Join-Path $root "artifacts/publish/$version"
$engine = Join-Path $stage "Impeller-Engine-$version-$RuntimeIdentifier"
$app = Join-Path $stage "Impeller-$version-$RuntimeIdentifier"

foreach ($dir in @($engine, $app)) {
    if (-not (Test-Path $dir)) { throw "not published: $dir  (drop -SkipPublish)" }
}

# Checked again here, over the folders actually about to be compressed. publish.ps1 already looks,
# but the failure this guards against - a missing .pri - surfaces only on the user's machine, inside
# LoadComponent, as a stowed WinRT exception naming no file at all. Worth asking twice.
$required = @{
    $engine = @('Impeller.EngineService.exe', 'appsettings.json')
    $app    = @('Impeller.exe', 'Impeller.pri', 'Microsoft.UI.Xaml.dll', 'Assets/AppIcon.ico')
}

foreach ($dir in $required.Keys) {
    $missing = $required[$dir] | Where-Object { -not (Test-Path (Join-Path $dir $_)) }
    if ($missing) { throw "missing from $dir : $($missing -join ', ')" }
}

$out = Join-Path $root 'artifacts/installer'
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host 'Compiling the installer...' -ForegroundColor Cyan

& $iscc `
    "/DVersion=$version" `
    "/DEngineDir=$engine" `
    "/DAppDir=$app" `
    "/O$out" `
    (Join-Path $root 'installer/Impeller.iss')

if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$setup = Join-Path $out "Impeller-$version-$RuntimeIdentifier-Setup.exe"
if (-not (Test-Path $setup)) { throw "ISCC reported success but produced nothing at $setup" }

# Beside the installer, because a download nobody can check is a download nobody should run.
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $setup -Leaf)" | Set-Content "$setup.sha256" -Encoding ascii

$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)

Write-Host ''
Write-Host ("{0}  ({1} MB)" -f $setup, $size) -ForegroundColor Green
Write-Host "  sha256 $hash" -ForegroundColor DarkGray
Write-Host 'Run it as administrator. It registers the engine service and starts it.' -ForegroundColor DarkGray
