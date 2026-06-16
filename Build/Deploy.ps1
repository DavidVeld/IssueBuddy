<#
.SYNOPSIS
    Deploys IssueBuddy DLL + .addin manifest to the correct Revit Addins folder.

.DESCRIPTION
    Run after a successful build.
    Copies the compiled DLL and matching .addin manifest to:
        %AppData%\Autodesk\Revit\Addins\<RevitVersion>\

.PARAMETER RevitVersion
    Which version to deploy: 2023, 2024, 2025, 2026, 2027  (default: all)

.PARAMETER Configuration
    Build configuration: Debug (default) or Release

.EXAMPLE
    .\Deploy.ps1
    .\Deploy.ps1 -RevitVersion 2025
    .\Deploy.ps1 -RevitVersion 2025 -Configuration Release
#>

param(
    [ValidateSet("2023","2024","2025","2026","2027","All")]
    [string]$RevitVersion = "All",

    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

# ── Framework map ──────────────────────────────────────────────────────────────
$frameworkMap = @{
    "2023" = @{ Framework = "net48";         BuildFolder = "Revit2023-2024"  }
    "2024" = @{ Framework = "net48";         BuildFolder = "Revit2023-2024"  }
    "2025" = @{ Framework = "net8-windows";  BuildFolder = "Revit2025-2027"  }
    "2026" = @{ Framework = "net8-windows";  BuildFolder = "Revit2025-2027"  }
    "2027" = @{ Framework = "net8-windows";  BuildFolder = "Revit2025-2027"  }
}

$versions = if ($RevitVersion -eq "All") { $frameworkMap.Keys | Sort-Object } else { @($RevitVersion) }

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appData     = $env:APPDATA

foreach ($ver in $versions) {
    $info        = $frameworkMap[$ver]
    $buildFolder = $info.BuildFolder
    $framework   = $info.Framework

    $srcDll    = Join-Path $projectRoot "bin\$Configuration\$buildFolder\IssueBuddy.dll"
    $srcAddin  = Join-Path $projectRoot "Build\Revit$ver\IssueBuddy.addin"
    $destDir   = Join-Path $appData "Autodesk\Revit\Addins\$ver"

    Write-Host "`nRevit $ver ($framework)" -ForegroundColor Cyan

    if (-not (Test-Path $srcDll)) {
        Write-Warning "  DLL not found: $srcDll  (build the project first)"
        continue
    }

    if (-not (Test-Path $destDir)) {
        Write-Host "  Creating target folder: $destDir"
        New-Item -ItemType Directory -Path $destDir | Out-Null
    }

    Copy-Item $srcDll   -Destination $destDir -Force
    Copy-Item $srcAddin -Destination $destDir -Force

    Write-Host "  ✓ Deployed to $destDir" -ForegroundColor Green
}

Write-Host "`nDone." -ForegroundColor Green
