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

    return "msbuild"
}

function Has-SdkPrefix([string]$prefix) {
    $sdks = & dotnet --list-sdks 2>$null
    foreach ($line in $sdks) {
        if ($line -like "$prefix*") {
            return $true
        }
    }
    return $false
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

if (-not $AutoCADPath) {
    $candidates = @(
        "${env:ProgramFiles}\Autodesk\AutoCAD 2025",
        "${env:ProgramFiles}\Autodesk\AutoCAD 2024"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            $AutoCADPath = $candidate
            break
        }
    }
}

if ($AutoCADPath) {
    $env:AUTOCAD_2025_PATH = $AutoCADPath
    $env:AUTOCAD_2024_PATH = $AutoCADPath
}

$msbuild = Resolve-MSBuild
$project = Join-Path $PSScriptRoot "src\AutoCAD.AITranslate\AutoCAD.AITranslate.csproj"

Write-Host "Using MSBuild: $msbuild"
Write-Host "Project: $project"
Write-Host "Configuration: $Configuration"
if ($env:AUTOCAD_2025_PATH) {
    Write-Host "AUTOCAD_2025_PATH: $env:AUTOCAD_2025_PATH"
} elseif ($env:AUTOCAD_2024_PATH) {
    Write-Host "AUTOCAD_2024_PATH: $env:AUTOCAD_2024_PATH"
}

if (-not (Has-SdkPrefix "8.")) {
    Write-Host "Warning: .NET 8 SDK not detected. Install it to build net8.0-windows projects."
}

$msbuildArgs = @(
    $project,
    "/restore",
    "/p:Configuration=$Configuration"
)

if ($AutoCADPath) {
    $msbuildArgs += "/p:AutoCADInstallDir=$AutoCADPath"
}

if (-not $OnlineRestore) {
    $offlineSource = "${env:ProgramFiles(x86)}\Microsoft SDKs\NuGetPackages\"
    if (Test-Path $offlineSource) {
        $offlineSource = Get-ShortPath $offlineSource
        $msbuildArgs += "/p:RestoreSources=$offlineSource"
    }
    $msbuildArgs += "/p:RestoreIgnoreFailedSources=true"
}

& $msbuild @msbuildArgs
