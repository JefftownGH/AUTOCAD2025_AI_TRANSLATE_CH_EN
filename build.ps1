param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$AutoCADPath = "",
    [switch]$OnlineRestore
)

$ErrorActionPreference = "Stop"

function Resolve-MSBuild {
    if ($env:MSBUILD_EXE_PATH -and (Test-Path $env:MSBUILD_EXE_PATH)) {
        return $env:MSBUILD_EXE_PATH
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        if ($installPath) {
            $candidate = Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    $fallback = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    if (Test-Path $fallback) {
        return $fallback
    }

    return $null
}

function Get-DotNetSdks {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        return @()
    }

    return @(& dotnet --list-sdks 2>$null)
}

function Get-ShortPath([string]$path) {
    if (-not $path -or -not (Test-Path $path)) {
        return $path
    }

    Add-Type -Namespace Win32 -Name PathUtil -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Auto)]
public static extern int GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, int cchBuffer);
'@

    $buffer = New-Object System.Text.StringBuilder 512
    $len = [Win32.PathUtil]::GetShortPathName($path, $buffer, $buffer.Capacity)
    if ($len -gt 0) {
        return $buffer.ToString()
    }

    return $path
}

# ---------------------------------------------------------------------------
# Locate the AutoCAD managed assemblies. Newest release first.
# ---------------------------------------------------------------------------
if (-not $AutoCADPath) {
    $candidates = @(
        "${env:ProgramFiles}\Autodesk\AutoCAD 2027",
        "${env:ProgramFiles}\Autodesk\AutoCAD 2026",
        "${env:ProgramFiles}\Autodesk\AutoCAD 2025",
        "${env:ProgramFiles}\Autodesk\AutoCAD 2024"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate "AcMgd.dll")) {
            $AutoCADPath = $candidate
            break
        }
    }
}

if (-not $AutoCADPath) {
    Write-Error @"
AutoCAD managed assemblies were not found.

Looked in:
$("  " + ((@(
    "${env:ProgramFiles}\Autodesk\AutoCAD 2027",
    "${env:ProgramFiles}\Autodesk\AutoCAD 2026",
    "${env:ProgramFiles}\Autodesk\AutoCAD 2025",
    "${env:ProgramFiles}\Autodesk\AutoCAD 2024"
)) -join "`n  "))

Pass the install directory explicitly, for example:
  .\build.ps1 -AutoCADPath "C:\Program Files\Autodesk\AutoCAD 2026"
"@
    exit 1
}

$env:AUTOCAD_PATH = $AutoCADPath
$env:AUTOCAD_2025_PATH = $AutoCADPath
$env:AUTOCAD_2024_PATH = $AutoCADPath

# ---------------------------------------------------------------------------
# Prerequisite checks, reported up front rather than as a confusing build error.
# ---------------------------------------------------------------------------
$sdks = Get-DotNetSdks
$hasNet8 = $false
foreach ($line in $sdks) {
    if ($line -like "8.*") {
        $hasNet8 = $true
        break
    }
}

# `dotnet build` from the SDK is sufficient on its own; MSBuild from Visual Studio is
# only needed as a fallback when no SDK is present. Requiring Visual Studio when the
# SDK is already installed would be an unnecessary demand.
$useDotNet = $hasNet8
$msbuild = $null
if (-not $useDotNet) {
    $msbuild = Resolve-MSBuild
}

Write-Host "AutoCAD assemblies : $AutoCADPath"
Write-Host "Configuration      : $Configuration"
Write-Host "dotnet SDKs        : $(if ($sdks.Count -gt 0) { ($sdks -join ', ') } else { 'none detected' })"
if ($useDotNet) {
    Write-Host "Build driver       : dotnet build (.NET SDK)"
} elseif ($msbuild) {
    Write-Host "Build driver       : MSBuild ($msbuild)"
} else {
    Write-Host "Build driver       : none available"
}

$blocking = @()
if (-not $useDotNet) {
    if ($msbuild) {
        $blocking += "No .NET 8 SDK was found, so the build falls back to a full MSBuild. Install the .NET 8 SDK for the supported path: https://dotnet.microsoft.com/download/dotnet/8.0"
    } else {
        $blocking += "The .NET 8 SDK is required to build a net8.0-windows project. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 (the .NET *runtime* alone is not sufficient). Visual Studio 2022 Build Tools with MSBuild is an alternative."
    }
}

if ($blocking.Count -gt 0) {
    Write-Host ""
    Write-Host "Cannot build yet:" -ForegroundColor Yellow
    foreach ($item in $blocking) {
        Write-Host "  - $item" -ForegroundColor Yellow
    }
    exit 1
}

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
$project = Join-Path $PSScriptRoot "src\JeffCAD.AiAssistant\JeffCAD.AiAssistant.csproj"
$restoreIgnore = "-p:RestoreIgnoreFailedSources=true"

if ($useDotNet) {
    $dotnetArgs = @(
        "build",
        $project,
        "-c", $Configuration,
        "-p:AutoCADInstallDir=$AutoCADPath"
    )

    if (-not $OnlineRestore) {
        $offlineSource = "${env:ProgramFiles(x86)}\Microsoft SDKs\NuGetPackages\"
        if (Test-Path $offlineSource) {
            $offlineSource = Get-ShortPath $offlineSource
            $dotnetArgs += "-p:RestoreSources=$offlineSource"
        }
        $dotnetArgs += $restoreIgnore
    }

    # The AutoCAD reference assemblies legitimately cause MSB3277 (conflicting versions
    # across Autodesk's own components). It is noise, not an actionable problem.
    $dotnetArgs += "-nowarn:MSB3277"

    & dotnet @dotnetArgs
} else {
    $msbuildArgs = @(
        $project,
        "/restore",
        "/p:Configuration=$Configuration",
        "/p:AutoCADInstallDir=$AutoCADPath",
        "/nowarn:MSB3277"
    )

    if (-not $OnlineRestore) {
        $offlineSource = "${env:ProgramFiles(x86)}\Microsoft SDKs\NuGetPackages\"
        if (Test-Path $offlineSource) {
            $offlineSource = Get-ShortPath $offlineSource
            $msbuildArgs += "/p:RestoreSources=$offlineSource"
        }
        $msbuildArgs += $restoreIgnore
    }

    & $msbuild @msbuildArgs
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

$output = Join-Path $PSScriptRoot "src\JeffCAD.AiAssistant\bin\$Configuration\net8.0-windows\JeffCAD.AiAssistant.dll"
Write-Host ""
Write-Host "Build succeeded." -ForegroundColor Green
Write-Host "Output: $output"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Copy JeffCAD.AiAssistant.settings.json.example to JeffCAD.AiAssistant.settings.json"
Write-Host "     next to the DLL and fill in LLM_API_KEY."
Write-Host "  2. In AutoCAD run NETLOAD and pick the DLL above."
