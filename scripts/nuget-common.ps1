<#
.SYNOPSIS
    Shared NuGet pack/push logic. Dot-sourced by per-package publish scripts.
.DESCRIPTION
    Provides Invoke-NuGetPublish, which handles:
      1. dotnet clean + dotnet pack (Release)
      2. dotnet nuget push --skip-duplicate to nuget.org

    No code signing — RedoxNet packages ship unsigned.

    Reads NUGET_API_KEY_REDOXNET from the environment when -NuGetApiKey is not
    passed.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Shared config ---
$script:RepoRoot = Split-Path $PSScriptRoot -Parent
$script:NuGetSource = 'https://api.nuget.org/v3/index.json'
$script:OutputDir = Join-Path $script:RepoRoot 'artifacts'

function Invoke-NuGetPublish {
    param(
        [Parameter(Mandatory)][string[]]$Projects,
        [switch]$SkipPush,
        [string]$NuGetApiKey,
        [string]$PackageVersion  # Optional /p:PackageVersion override.
    )

    # --- Clean output dir ---
    if (Test-Path $script:OutputDir) {
        Remove-Item $script:OutputDir -Recurse -Force
    }
    New-Item $script:OutputDir -ItemType Directory | Out-Null

    # --- 1. Clean + Pack ---
    Write-Host "`n=== Packing (clean build, Release) ===" -ForegroundColor Cyan
    foreach ($proj in $Projects) {
        $projPath = Join-Path $script:RepoRoot $proj
        if (-not (Test-Path $projPath)) {
            Write-Error "Project not found: $projPath"
        }
        Write-Host "  Cleaning $proj ..."
        dotnet clean $projPath -c Release --nologo -v q
        Write-Host "  Packing $proj ..."
        $packArgs = @($projPath, '-c', 'Release', '-o', $script:OutputDir)
        if ($PackageVersion) {
            Write-Host "    (PackageVersion override: $PackageVersion)"
            $packArgs += "/p:PackageVersion=$PackageVersion"
        }
        dotnet pack @packArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Pack failed for $proj"
        }
    }

    $packages = Get-ChildItem $script:OutputDir -Filter '*.nupkg' | Sort-Object Name
    if (-not $packages) {
        Write-Error "No .nupkg files produced under $script:OutputDir"
    }
    Write-Host "`n  Packages created:" -ForegroundColor Green
    $packages | ForEach-Object {
        Write-Host "    $($_.Name)  ($([math]::Round($_.Length / 1KB, 1)) KB)"
    }

    # --- 2. Push ---
    if ($SkipPush) {
        Write-Host "`n  -SkipPush set — stopping after pack." -ForegroundColor Yellow
        return
    }

    Write-Host "`n=== Pushing to nuget.org ===" -ForegroundColor Cyan
    if (-not $NuGetApiKey) {
        $NuGetApiKey = $env:NUGET_API_KEY_REDOXNET
    }
    if (-not $NuGetApiKey) {
        Write-Error "NuGet API key required. Pass -NuGetApiKey or set NUGET_API_KEY_REDOXNET env var."
    }

    $keyTail = if ($NuGetApiKey.Length -ge 4) { $NuGetApiKey.Substring($NuGetApiKey.Length - 4) } else { '****' }
    Write-Host "  Using API key ****$keyTail"

    foreach ($pkg in $packages) {
        Write-Host "  Pushing $($pkg.Name) ..."
        dotnet nuget push $pkg.FullName `
            --api-key $NuGetApiKey `
            --source $script:NuGetSource `
            --skip-duplicate
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Push failed for $($pkg.Name)"
        }
    }
    Write-Host "  All packages pushed." -ForegroundColor Green
    Write-Host "`n=== Done ===" -ForegroundColor Cyan
}
