#Requires -Version 5.1
# Builds BetterJoy (Release|x64) and, if Inno Setup is installed, packages it into a setup executable.
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$solution = Join-Path $repoRoot "BetterJoy.sln"
$toolsDir = Join-Path $repoRoot ".tools"
$nugetExe = Join-Path $toolsDir "nuget.exe"
$issFile = Join-Path $repoRoot "Installer\BetterJoy.iss"

function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "Visual Studio Installer not found. Install Visual Studio (or Build Tools) with the '.NET desktop development' workload."
    }
    $msbuildPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    if (-not $msbuildPath) {
        throw "MSBuild not found via vswhere. Make sure the '.NET desktop development' workload is installed."
    }
    return $msbuildPath
}

function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

Write-Host "==> Locating MSBuild..."
$msbuild = Find-MSBuild
Write-Host "    $msbuild"

if (-not (Test-Path $nugetExe)) {
    Write-Host "==> Downloading nuget.exe..."
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $nugetExe
}

Write-Host "==> Restoring NuGet packages..."
& $nugetExe restore $solution -Source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed." }

Write-Host "==> Building $solution (Release|x64)..."
& $msbuild $solution -p:Configuration=Release -p:Platform=x64 -t:Rebuild -m -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "==> Build succeeded: BetterJoyForCemu\bin\x64\Release\BetterJoy2.exe"

$iscc = Find-Iscc
if ($iscc) {
    Write-Host "==> Inno Setup found ($iscc), packaging installer..."
    & $iscc $issFile
    if ($LASTEXITCODE -ne 0) { throw "Installer packaging failed." }
    Write-Host "==> Installer created in Installer\Output\"
} else {
    Write-Host "==> Inno Setup not found - skipping installer packaging."
    Write-Host "    Install it from https://jrsoftware.org/isdl.php to enable this step."
}

Write-Host "==> Done."
