#Requires -Version 7
<#
.SYNOPSIS
    Packs the plugin SDK into the local folder feed and builds the sample against it.

.DESCRIPTION
    The sample is the first consumer of this SDK that is not inside the solution, which is the only
    honest test of one. Building it from a project reference would hide exactly the problems an
    outside author hits first, so it restores the real package from artifacts/packages instead.
#>
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $root 'artifacts/packages'

New-Item -ItemType Directory -Force -Path $feed | Out-Null

# Both, and this is not a detail: the SDK declares the abstractions as a package dependency, so a
# feed holding only the SDK restores to NU1101 - which is exactly the failure an outside author
# would have hit first, and exactly what a project reference in the sample would have hidden.
foreach ($project in @('Impeller.Plugins.Abstractions', 'Impeller.Plugins.Sdk')) {
    Write-Host "Packing $project..." -ForegroundColor Cyan
    dotnet pack (Join-Path $root "src/$project/$project.csproj") -c $Configuration -o $feed --nologo
    if ($LASTEXITCODE -ne 0) { throw "pack failed: $project" }
}

# The restore cache would otherwise serve yesterday's package for the same version number, which is
# the single most confusing failure mode of a local folder feed.
foreach ($cached in @('impeller.plugins.sdk', 'impeller.plugins.abstractions')) {
    $path = Join-Path $env:USERPROFILE ".nuget/packages/$cached"
    if (Test-Path $path) {
        Write-Host "Clearing the cached copy of $cached..." -ForegroundColor Cyan
        Remove-Item $path -Recurse -Force
    }
}

Write-Host 'Building the sample against the packaged SDK...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'samples/Impeller.Plugin.Sample/Impeller.Plugin.Sample.csproj') `
    -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'sample build failed' }

Write-Host 'Done.' -ForegroundColor Green
