# Verifies that the deployed runtime contains only LGPL-compatible Qt parts (whitelist; unknown => fail).
# Usage: tools/check-licenses.ps1 [-Rid win-arm64|win-x64 | -Path <native dir>] [-SkipDocs]
# Without -Rid/-Path every runtimes/win-*/native is checked (Linux bundles: tools/check-licenses.sh). Exit code 1 on any violation.
param(
    [string]$Rid,
    [string]$Path,
    [switch]$SkipDocs
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path

# --- Whitelist (LGPL-3.0 / permissive). Extend only after checking the module's license on qt.io. ---
# Qt modules: current deploy + modules planned for v1 (QuickEffects for MultiEffect, Layouts, ...).
$qtAllowed = ('Core Gui Network OpenGL Qml QmlMeta QmlModels QmlWorkerScript QmlCore QmlLocalStorage QmlXmlListModel ' +
              'Quick QuickShapes QuickEffects QuickLayouts QuickTemplates2 QuickControls2 QuickControls2Basic ' +
              'QuickControls2Impl QuickControls2BasicStyleImpl ShaderTools Svg Concurrent Multimedia MultimediaQuick') -split ' ' | ForEach-Object { "Qt6$_.dll" }
# Known GPL-only modules: listed only to give a precise message (everything not whitelisted fails anyway).
$qtDenied = '^Qt6(Quick3D\w*|Charts\w*|DataVisualization\w*|Graphs\w*|QuickTimeline\w*|VirtualKeyboard\w*|Mqtt|Coap|Opcua\w*)\.dll$'
# Non-Qt DLLs we ship or may get from windeployqt.
$otherAllowed = 'vlqt.dll', 'D3Dcompiler_47.dll', 'dxcompiler.dll', 'dxil.dll', 'opengl32sw.dll',
                'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'concrt140.dll'
# FFmpeg shared libraries of Qt Multimedia (FFmpeg backend). LGPL-2.1-or-later only: each DLL must carry FFmpeg's
# license string 'LGPL version 2.1 or later' and must not be configured with --enable-gpl / --enable-nonfree.
$ffmpegDll = '^(avcodec|avformat|avutil|swresample|swscale)-\d+\.dll$'
# Plugin dirs and QML modules (relative to native\). A DLL outside these directories is rejected.
$pluginDirs = 'platforms', 'imageformats', 'iconengines', 'generic', 'tls', 'networkinformation', 'qmltooling', 'multimedia'
$qmlModules = 'QML', 'QtQml', 'QtQml\Models', 'QtQml\WorkerScript', 'QtQuick', 'QtQuick\Shapes', 'QtQuick\Effects',
              'QtQuick\Layouts', 'QtQuick\Templates', 'QtQuick\Window', 'QtMultimedia',
              'QtQuick\Controls', 'QtQuick\Controls\impl', 'QtQuick\Controls\Basic', 'QtQuick\Controls\Basic\impl' # Button; style VLQt (embedded in vlqt) falls back to Basic and uses Basic.impl (#213, #292)
# Required in the repo for LGPL compliance.
$requiredDocs = 'LICENSE', 'LICENSES.md', 'LICENSES\LGPL-3.0.txt', 'LICENSES\LGPL-2.1.txt', 'LICENSES\GPL-3.0.txt', 'LICENSES\Qt-NOTICE.md'

$natives = if ($Path) { @((Resolve-Path $Path).Path) }
           elseif ($Rid) { @("$root\runtimes\$Rid\native") }
           else { @(Get-ChildItem "$root\runtimes\win-*\native" -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName) }
if (-not $natives) { throw 'No runtimes/*/native directory found' }

$errors = [System.Collections.Generic.List[string]]::new()
function Fail($m) { $script:errors.Add($m); Write-Host "FAIL  $m" -ForegroundColor Red }

foreach ($n in $natives) {
    if (-not (Test-Path $n)) { Fail "missing $n"; continue }
    $n = (Resolve-Path $n).Path
    Write-Host "Checking $n"
    $versions = @{}
    foreach ($f in Get-ChildItem $n -Recurse -File -Filter *.dll) {
        $rel = $f.FullName.Substring($n.Length + 1)
        $dir = Split-Path $rel -Parent
        $name = $f.Name
        if ($name -like 'Qt6*.dll') {
            if ($dir) { Fail "$rel : Qt module outside the native root" }
            elseif ($name -match $qtDenied) { Fail "$rel : GPL-only Qt module" }
            elseif ($name -notin $qtAllowed) { Fail "$rel : Qt module not on the whitelist" }
            $versions[$f.VersionInfo.ProductVersion] = $true
        }
        elseif (-not $dir -and $name -match $ffmpegDll) {
            $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($f.FullName))
            if (-not $text.Contains('LGPL version 2.1 or later')) { Fail "$rel : FFmpeg library without the LGPL-2.1+ license string" }
            if ($text.Contains('--enable-gpl') -or $text.Contains('--enable-nonfree')) { Fail "$rel : FFmpeg built with --enable-gpl/--enable-nonfree" }
        }
        elseif (-not $dir) {
            if ($name -notin $otherAllowed) { Fail "$rel : unknown DLL in native root" }
        }
        elseif ($dir -like 'plugins\*') {
            $top = ($dir -split '\\')[1]
            if ($top -notin $pluginDirs) { Fail "$rel : plugin directory '$top' not on the whitelist" }
        }
        elseif ($dir -like 'qml\*') {
            $mod = $dir.Substring(4)
            if ($mod -notin $qmlModules) { Fail "$rel : QML module '$mod' not on the whitelist" }
        }
        else { Fail "$rel : DLL in unexpected location" }
    }
    # Module folders without a DLL (qmldir only) are checked too.
    foreach ($d in Get-ChildItem "$n\qml" -Recurse -Directory -ErrorAction SilentlyContinue) {
        $mod = $d.FullName.Substring("$n\qml\".Length)
        if ((Test-Path "$($d.FullName)\qmldir") -and $mod -notin $qmlModules) { Fail "qml\$mod : QML module not on the whitelist" }
    }
    if ($versions.Count -gt 1) { Fail "Qt modules have mixed versions: $($versions.Keys -join ', ')" }
    elseif ($versions.Count -eq 1) { Write-Host "  Qt version: $($versions.Keys)" }
    if (-not (Test-Path "$n\vlqt.dll")) { Fail 'vlqt.dll missing' }
}
if (-not $SkipDocs) {
    foreach ($d in $requiredDocs) { if (-not (Test-Path "$root\$d")) { Fail "missing license file: $d" } }
}
if ($errors.Count) { Write-Host "$($errors.Count) license check violation(s)" -ForegroundColor Red; exit 1 }
Write-Host 'License check OK'
