# Trims runtimes/<rid>/native after windeployqt: removes Qt parts VL.Qt never loads.
# Usage: tools/trim-deploy.ps1 [-Rid win-arm64|win-x64] [-Path <native dir>] [-DropSvg] [-KeepQmlTooling] [-WhatIf]
# Idempotent. Run order: build-native.ps1 -> trim-deploy.ps1 -> check-licenses.ps1 -> SmokeTest.
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('win-arm64', 'win-x64')] [string]$Rid = 'win-arm64',
    [string]$Path,
    # Qt6Svg + SVG image/icon plugins (~0.6 MB): kept by default, Image{source: *.svg} is plausible for graphics work.
    [switch]$DropSvg,
    # QML debugger/profiler plugins: removed by default; keep for debug builds (-qmljsdebugger).
    [switch]$KeepQmlTooling
)
$ErrorActionPreference = 'Stop'

$native = if ($Path) { $Path } else { "$PSScriptRoot\..\runtimes\$Rid\native" }
$native = (Resolve-Path $native).Path

# Each entry: relative path (wildcards allowed) + reason. Verified against the SmokeTest.
$remove = @(
    @{ P = 'plugins\tls';                 R = 'TLS backends (schannel/certonly): VL.Qt loads local files only, no https. Qt6Network itself stays (imported by Qt6Qml/Qt6Quick).' }
    @{ P = 'plugins\networkinformation';  R = 'Network reachability plugin: only used by QNetworkInformation, nothing in VL.Qt calls it.' }
    @{ P = 'plugins\generic';             R = 'TUIO touch plugin: not wanted, input arrives via vvvv/Qt events.' }
    @{ P = 'plugins\multimedia\windowsmediaplugin.dll'; R = 'Windows Media Foundation backend: Video uses the FFmpeg backend (Qt default on Windows); without it a missing FFmpeg DLL fails visibly instead of silently switching backends.' }
    @{ P = 'vlqt.ilk';                    R = 'Incremental linker state of the dev build, not a runtime file.' }
    @{ P = 'qml\*\plugins.qmltypes';      R = 'Type metadata for tooling (qmllint/qmlls); the engine loads types from the plugin DLLs.' }
    @{ P = 'qml\*\*\plugins.qmltypes';    R = 'same as above, nested modules.' }
    @{ P = 'qml\*\*\*\plugins.qmltypes';  R = 'same as above, nested modules (QtQuick\Controls\Basic, ...).' }
    @{ P = 'qml\*\*\*\*\plugins.qmltypes'; R = 'same as above, nested modules (QtQuick\Controls\Basic\impl).' }
)
# Qt Quick Controls (Button, #213): only Basic is deployed: our own style VLQt (embedded in vlqt) uses it as fallback and uses Basic.impl (QQuickStyle::setStyle in vlqt.cpp). windeployqt
# deploys every style that QtQuick/Controls/qmldir lists as optional import; those imports are only resolved for the
# selected style, so the others can go. Milestone "styles & theming" (#298) decides about further styles.
foreach ($style in 'Fusion', 'Imagine', 'Material', 'Universal', 'FluentWinUI3', 'Windows', 'Native', 'macOS', 'iOS') {
    $remove += @{ P = "qml\QtQuick\Controls\$style"; R = "Controls style ${style}: not used, only Basic is (#213)." }
    $remove += @{ P = "Qt6QuickControls2$style*.dll"; R = "Controls style $style libraries (only imported by its style plugin)." }
}
$remove += @{ P = 'qml\QtQuick\NativeStyle';  R = 'Native style base (Windows/macOS styles), not used (#213).' }
$remove += @{ P = 'Qt6QuickNativeStyle.dll';  R = 'Only imported by the NativeStyle/Windows style plugins (dumpbin /dependents).' }
$remove += @{ P = 'qml\QtQuick\Layouts';      R = 'QtQuick.Layouts: only pulled in by the removed styles, no element uses it.' }
$remove += @{ P = 'Qt6QuickLayouts.dll';      R = 'Only imported by the Layouts plugin and the removed styles (dumpbin /dependents).' }
$remove += @{ P = 'qml\QtQuick\Controls\*.qml'; R = 'Loose QML sources; qmldir prefer :/qt-project.org/... loads the copies compiled into the plugin DLLs.' }
$remove += @{ P = 'qml\QtQuick\Controls\*\*.qml'; R = 'same as above (Basic, impl).' }
$remove += @{ P = 'qml\QtQuick\Controls\*\*\*.qml'; R = 'same as above (Basic\impl).' }
if (-not $KeepQmlTooling) {
    $remove += @{ P = 'plugins\qmltooling'; R = 'QML debugger/profiler/inspector plugins (-qmljsdebugger); not needed in release builds.' }
}
if ($DropSvg) {
    $remove += @{ P = 'Qt6Svg.dll';                    R = 'Only imported by the SVG plugins below (dumpbin /dependents).' }
    $remove += @{ P = 'plugins\imageformats\qsvg.dll'; R = 'SVG image format, needs Qt6Svg.' }
    $remove += @{ P = 'plugins\iconengines';           R = 'SVG icon engine, needs Qt6Svg.' }
}
# NOT removable (hard imports, dumpbin /dependents): Qt6OpenGL (Qt6Quick), Qt6Network (Qt6Qml, Qt6Quick), Qt6QmlMeta, Qt6QmlModels.

function Get-Mb($p) { [math]::Round((Get-ChildItem $p -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 2) }
$before = Get-Mb $native
$count = 0
foreach ($e in $remove) {
    foreach ($item in @(Get-Item -Path (Join-Path $native $e.P) -ErrorAction SilentlyContinue)) {
        $rel = $item.FullName.Substring($native.Length + 1)
        if ($PSCmdlet.ShouldProcess($rel, "remove ($($e.R))")) {
            Remove-Item $item.FullName -Recurse -Force
            Write-Host "removed  $rel"
        }
        $count++
    }
}
# Drop now-empty directories (windeployqt leaves them behind).
if (-not $WhatIfPreference) {
    do {
        $empty = @(Get-ChildItem $native -Recurse -Directory | Where-Object { -not (Get-ChildItem $_.FullName -Force) })
        $empty | ForEach-Object { Remove-Item $_.FullName }
    } while ($empty.Count)
}

$after = Get-Mb $native
Write-Host "Trimmed $native : $count item(s), $before MB -> $after MB"
