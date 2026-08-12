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
# SubPath is what MSBuild appends to OutputPath: the target framework, plus the
# runtime identifier for net8-windows (which sets RuntimeIdentifier=win-x64).
$frameworkMap = @{
    "2023" = @{ Framework = "net48";         BuildFolder = "Revit2023-2024";  SubPath = "net48"                }
    "2024" = @{ Framework = "net48";         BuildFolder = "Revit2023-2024";  SubPath = "net48"                }
    "2025" = @{ Framework = "net8-windows";  BuildFolder = "Revit2025-2027";  SubPath = "net8-windows\win-x64" }
    "2026" = @{ Framework = "net8-windows";  BuildFolder = "Revit2025-2027";  SubPath = "net8-windows\win-x64" }
    "2027" = @{ Framework = "net8-windows";  BuildFolder = "Revit2025-2027";  SubPath = "net8-windows\win-x64" }
}

# Resolves the build output folder, tolerating either layout: with the framework
# subfolder MSBuild appends by default, or flattened if that is ever disabled.
function Resolve-BuildDir {
    param([string]$Root, [string]$SubPath)

    $nested = Join-Path $Root $SubPath
    if (Test-Path (Join-Path $nested "IssueBuddy.dll")) { return $nested }
    return $Root
}

$versions = if ($RevitVersion -eq "All") { $frameworkMap.Keys | Sort-Object } else { @($RevitVersion) }

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appData     = $env:APPDATA

foreach ($ver in $versions) {
    $info        = $frameworkMap[$ver]
    $buildFolder = $info.BuildFolder
    $framework   = $info.Framework

    $buildRoot = Join-Path $projectRoot "bin\$Configuration\$buildFolder"
    $srcDir    = Resolve-BuildDir -Root $buildRoot -SubPath $info.SubPath

    $srcDll    = Join-Path $srcDir "IssueBuddy.dll"
    $srcHelp   = Join-Path $srcDir "ReadMe - issueTools.pdf"
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

    # F1 contextual help resolves the PDF relative to the deployed DLL, so it
    # has to travel with it — without this, F1 silently does nothing.
    if (Test-Path $srcHelp) {
        Copy-Item $srcHelp -Destination $destDir -Force
    } else {
        Write-Warning "  Help PDF not found: $srcHelp  (F1 help will be unavailable)"
    }

    Write-Host "  ✓ Deployed to $destDir" -ForegroundColor Green
}

Write-Host "`nDone." -ForegroundColor Green
