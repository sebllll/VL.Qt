# Builds QtNative and deploys Qt into runtimes/<rid>/native (windeployqt runs as CMake target vlqt_deploy).
# Usage: tools/build-native.ps1 [-Rid win-arm64|win-x64 (default: host)] [-QtDir C:\Qt\6.12.0\msvc2022_arm64] [-ForceDeploy]
# Configures CMake directly (same settings as the dev-<rid> preset) so CI can point at any Qt location.
# windeployqt re-runs when vlqt.dll or qml/deploy.qml changed, when the deployed qml/QtQuick/Effects or qml/QtMultimedia
# is missing, or with -ForceDeploy.
param(
    [ValidateSet('win-arm64', 'win-x64')] [string]$Rid,
    [string]$QtDir,
    [switch]$ForceDeploy
)
$ErrorActionPreference = 'Stop'
# Machine value: the process value says AMD64 in an x64-emulated shell on arm64.
if (-not $Rid) { $Rid = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'win-arm64' } else { 'win-x64' } }

$QtVersion = '6.12.0'
$qtSub = @{ 'win-arm64' = 'msvc2022_arm64'; 'win-x64' = 'msvc2022_64' }[$Rid]
if (-not $QtDir) { $QtDir = "C:\Qt\$QtVersion\$qtSub" }
if (-not (Test-Path "$QtDir\bin\windeployqt.exe")) { throw "Qt not found in $QtDir (run tools/setup-qt.ps1 -Rid $Rid)" }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$component = if ($Rid -eq 'win-arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
$vsInstallHint = "winget install --id Microsoft.VisualStudio.2022.BuildTools --override `"--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --add $component --includeRecommended`""
if (-not (Test-Path $vswhere)) { throw "Visual Studio / Build Tools not installed ($vswhere missing). Install with:`n  $vsInstallHint" }
$vs = & $vswhere -latest -prerelease -products * -requires $component -property installationPath
if (-not $vs) { throw "Visual Studio with C++ tools ($component) not found. Install with:`n  $vsInstallHint" }

$vcvars = "$vs\VC\Auxiliary\Build\vcvarsall.bat"
$hostArch = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'arm64' } else { 'x64' }
$vcArch = switch ($Rid) {
    'win-arm64' { if ($hostArch -eq 'arm64') { 'arm64' } else { 'x64_arm64' } }
    'win-x64'   { if ($hostArch -eq 'arm64') { 'arm64_x64' } else { 'x64' } }
}

$src = (Resolve-Path "$PSScriptRoot\..\src\VL.Qt.Native").Path
$bin = Join-Path (Resolve-Path "$PSScriptRoot\..").Path "build\$Rid"
$prefix = $QtDir.Replace('\', '/')
$outDir = Join-Path (Resolve-Path "$PSScriptRoot\..").Path "runtimes\$Rid\native"
# Deleting the stamp makes Ninja re-run windeployqt even if vlqt.dll and deploy.qml are unchanged.
$stamp = "$bin\windeployqt.stamp"
if ((Test-Path $stamp) -and ($ForceDeploy -or -not (Test-Path "$outDir\qml\QtQuick\Effects\qmldir") -or -not (Test-Path "$outDir\qml\QtMultimedia\qmldir") -or -not (Test-Path "$outDir\qml\QtQuick\Controls\Basic\qmldir"))) {
    Remove-Item $stamp
    Write-Host 'Forcing windeployqt'
}
# vcvarsall puts the VS-bundled cmake and ninja on PATH.
# Windows PowerShell 5.1 turns redirected native stderr (tools/build.ps1 captures *>&1) into error records, so with
# Stop the first CMake warning would abort the build. The exit code decides instead.
$ErrorActionPreference = 'Continue'
cmd /c "`"$vcvars`" $vcArch >nul && cmake -S `"$src`" -B `"$bin`" -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo -DCMAKE_PREFIX_PATH=`"$prefix`" -DVLQT_RID=$Rid -DCMAKE_EXPORT_COMPILE_COMMANDS=ON && cmake --build `"$bin`""
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE) { throw "native build failed ($LASTEXITCODE)" }

$out = Resolve-Path $outDir
# Modules imported by qml/deploy.qml must arrive with content (an empty folder means windeployqt missed them).
foreach ($m in 'QtQuick', 'QtQuick\Effects', 'QtMultimedia', 'QtQuick\Controls', 'QtQuick\Controls\Basic', 'QtQuick\Templates') {
    if (-not (Test-Path "$out\qml\$m\qmldir")) { throw "deploy incomplete: qml\$m\qmldir missing" }
}
# Video (QtMultimedia): only the FFmpeg backend is used; its plugin needs the FFmpeg DLLs next to the Qt DLLs.
foreach ($f in 'plugins\multimedia\ffmpegmediaplugin.dll', 'avcodec-*.dll', 'avformat-*.dll', 'avutil-*.dll', 'swresample-*.dll', 'swscale-*.dll') {
    if (-not (Test-Path "$out\$f")) { throw "deploy incomplete: $f missing (Qt Multimedia installed? tools/setup-qt.ps1)" }
}
$mb = [math]::Round((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "Deployed to $out ($mb MB)"
