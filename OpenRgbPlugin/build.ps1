#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$pluginRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $pluginRoot
$toolsRoot = Join-Path $repoRoot ".tools\openrgb-plugin"
$downloadsRoot = Join-Path $toolsRoot "downloads"
$qtRoot = Join-Path $toolsRoot "qt-5.15.0"
$openRgbRoot = Join-Path $toolsRoot "OpenRGB-release_candidate_1.0rc3"
$outputRoot = Join-Path $pluginRoot "bin\Release"
$project = Join-Path $pluginRoot "BetterJoyOpenRgbPlugin.pro"

$qtArchiveName = "5.15.0-0-202005150700qtbase-Windows-Windows_10-MSVC2019-Windows-Windows_10-X86_64.7z"
$qtArchive = Join-Path $downloadsRoot $qtArchiveName
$qtUrl = "https://download.qt.io/online/qtsdkrepository/windows_x86/desktop/qt5_5150/qt.qt5.5150.win64_msvc2019_64/$qtArchiveName"
$openRgbArchive = Join-Path $downloadsRoot "OpenRGB-release_candidate_1.0rc3.zip"
$openRgbUrl = "https://github.com/CalcProgrammer1/OpenRGB/archive/refs/tags/release_candidate_1.0rc3.zip"

function Find-VcVars64 {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "Visual Studio Installer was not found. Install the Desktop development with C++ workload."
    }

    $installation = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath | Select-Object -First 1
    if (-not $installation) {
        throw "The Visual C++ x64 build tools are missing. Install the Desktop development with C++ workload."
    }

    $vcVars = Join-Path $installation "VC\Auxiliary\Build\vcvars64.bat"
    if (-not (Test-Path -LiteralPath $vcVars)) {
        throw "vcvars64.bat was not found under $installation."
    }
    return $vcVars
}

function Ensure-Download([string]$Uri, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Write-Host "    Downloading $Uri"
    Invoke-WebRequest -Uri $Uri -OutFile $Destination
}

function Ensure-Qt {
    $existing = Get-ChildItem -LiteralPath $qtRoot -Recurse -Filter qmake.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'msvc2019_64\\bin\\qmake\.exe$' } |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $existing) {
        Ensure-Download $qtUrl $qtArchive
        New-Item -ItemType Directory -Force -Path $qtRoot | Out-Null
        Write-Host "    Extracting Qt 5.15.0..."
        & tar.exe -xf $qtArchive -C $qtRoot
        if ($LASTEXITCODE -ne 0) { throw "Qt extraction failed." }

        $existing = Get-ChildItem -LiteralPath $qtRoot -Recurse -Filter qmake.exe |
            Where-Object { $_.FullName -match 'msvc2019_64\\bin\\qmake\.exe$' } |
            Select-Object -First 1 -ExpandProperty FullName
        if (-not $existing) { throw "qmake.exe was not found after extracting Qt." }
    }

    # Qt's public online archive contains the same LGPL/GPL binaries as the online installer,
    # but its raw qconfig.pri is stamped Enterprise and expects a commercial-account license
    # file that the archive intentionally does not contain. This is the same edition fix-up
    # performed by aqtinstall for open-source installations; no Qt runtime is redistributed.
    $qconfig = Join-Path (Split-Path -Parent (Split-Path -Parent $existing)) "mkspecs\qconfig.pri"
    $qconfigText = Get-Content -LiteralPath $qconfig -Raw
    $qconfigText = $qconfigText -replace 'QT_EDITION\s*=\s*Enterprise', 'QT_EDITION = OpenSource'
    $qconfigText = $qconfigText -replace '(?m)^QT_LICHECK\s*=.*\r?\n', ''
    Set-Content -LiteralPath $qconfig -Value $qconfigText -Encoding ASCII
    return $existing
}

function Ensure-OpenRgbSource {
    if (Test-Path -LiteralPath (Join-Path $openRgbRoot "OpenRGBPluginInterface.h")) {
        return $openRgbRoot
    }

    Ensure-Download $openRgbUrl $openRgbArchive
    Write-Host "    Extracting OpenRGB release_candidate_1.0rc3 headers..."
    Expand-Archive -LiteralPath $openRgbArchive -DestinationPath $toolsRoot -Force
    if (-not (Test-Path -LiteralPath (Join-Path $openRgbRoot "OpenRGBPluginInterface.h"))) {
        throw "The pinned OpenRGB source archive did not contain the expected rc3 tree."
    }
    return $openRgbRoot
}

$vcVars64 = Find-VcVars64
$qmake = Ensure-Qt
$openRgbSource = Ensure-OpenRgbSource
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$qmakeBuildRoot = Join-Path $pluginRoot "obj\qmake"
New-Item -ItemType Directory -Force -Path $qmakeBuildRoot | Out-Null

$qmakeNative = $qmake -replace '/', '\'
$projectNative = $project -replace '/', '\'
$openRgbNative = $openRgbSource -replace '\\', '/'
$outputNative = $outputRoot -replace '\\', '/'
$vcVarsNative = $vcVars64 -replace '/', '\'

Write-Host "==> Building BetterJoy2 OpenRGB plugin (API v4 / Qt 5.15.0)..."
$command = 'call "' + $vcVarsNative + '" && "' + $qmakeNative + '" "' + $projectNative +
    '" OPENRGB_SOURCE_DIR="' + $openRgbNative + '" BETTERJOY_PLUGIN_OUTPUT_DIR="' +
    $outputNative + '" && nmake /NOLOGO'
Push-Location $qmakeBuildRoot
try {
    & cmd.exe /d /c $command
    if ($LASTEXITCODE -ne 0) { throw "OpenRGB plugin build failed." }
} finally {
    Pop-Location
}

$pluginDll = Join-Path $outputRoot "BetterJoyOpenRgbPlugin.dll"
if (-not (Test-Path -LiteralPath $pluginDll)) {
    throw "OpenRGB plugin build completed without producing $pluginDll."
}

Write-Host "==> OpenRGB plugin built: $pluginDll"
