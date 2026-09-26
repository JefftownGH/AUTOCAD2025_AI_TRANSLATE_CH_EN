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
$msbuild = Resolve-MSBuild
$sdks = Get-DotNetSdks
$hasNet8 = $false
foreach ($line in $sdks) {
    if ($line -like "8.*") {
        $hasNet8 = $true
        break
    }
}

Write-Host "AutoCAD assemblies : $AutoCADPath"
Write-Host "Configuration      : $Configuration"
if ($msbuild) {
    Write-Host "MSBuild            : $msbuild"
} else {
    Write-Host "MSBuild            : not found on this machine"
}
Write-Host "dotnet SDKs        : $(if ($sdks.Count -gt 0) { ($sdks -join ', ') } else { 'none detected' })"

$blocking = @()
if (-not $hasNet8) {
    $blocking += "The .NET 8 SDK is required to build a net8.0-windows project. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 (the .NET *runtime* alone is not sufficient)."
}
if (-not $msbuild) {
    $blocking += "MSBuild was not found. Install Visual Studio 2022 (Desktop development with C++ / .NET desktop workload) or the standalone Build Tools."
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
$project = Join-Path $PSScriptRoot "src\AutoCAD.AITranslate\AutoCAD.AITranslate.csproj"

$msbuildArgs = @(
    $project,
    "/restore",
    "/p:Configuration=$Configuration",
    "/p:AutoCADInstallDir=$AutoCADPath"
)

if (-not $OnlineRestore) {
    $offlineSource = "${env:ProgramFiles(x86)}\Microsoft SDKs\NuGetPackages\"
    if (Test-Path $offlineSource) {
        $offlineSource = Get-ShortPath $offlineSource
        $msbuildArgs += "/p:RestoreSources=$offlineSource"
    }
    $msbuildArgs += "/p:RestoreIgnoreFailedSources=true"
}

& $msbuild @msbuildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

$output = Join-Path $PSScriptRoot "src\AutoCAD.AITranslate\bin\$Configuration\net8.0-windows\AutoCAD.AITranslate.dll"
Write-Host ""
Write-Host "Build succeeded." -ForegroundColor Green
Write-Host "Load in AutoCAD with NETLOAD, then pick:"
Write-Host "  $output"
