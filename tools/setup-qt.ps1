# Installs the pinned Qt version via aqtinstall (LGPL modules only): base + Qt Multimedia (Video element, FFmpeg backend).
# Usage: tools/setup-qt.ps1 [-Rid win-arm64|win-x64 (default: host)] [-OutDir C:\Qt] [-DebugSymbols]
# -DebugSymbols adds the Qt PDBs of qtbase, qtdeclarative and qtmultimedia (profiling/debugging, e.g. Ultra #557;
# several GB, never shipped). Without it, debug symbols are skipped.
param(
    [ValidateSet('win-arm64', 'win-x64')] [string]$Rid,
    [string]$OutDir = 'C:\Qt',
    [switch]$DebugSymbols
)
$ErrorActionPreference = 'Stop'
# Machine value: the process value says AMD64 in an x64-emulated shell on arm64.
if (-not $Rid) { $Rid = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'win-arm64' } else { 'win-x64' } }

$QtVersion = '6.12.0'
$map = @{
    'win-arm64' = @{ Host = 'windows_arm64'; Arch = 'win64_msvc2022_arm64'; Dir = 'msvc2022_arm64' }
    'win-x64'   = @{ Host = 'windows';       Arch = 'win64_msvc2022_64';    Dir = 'msvc2022_64';    Repo = 'qt6_6120_msvc2022_64' }
}
$t = $map[$Rid]
$qtDir = Join-Path $OutDir "$QtVersion\$($t.Dir)"
$hasBase = Test-Path "$qtDir\bin\qtpaths6.exe"
$hasShaderTools = Test-Path "$qtDir\bin\qsb.exe" # qsb (qtshadertools) bakes the ShaderEffect shaders at build time (#437), never shipped
# Qt Multimedia ships the FFmpeg DLLs (avcodec-*.dll, ...) in bin/ next to Qt6Multimedia.dll.
$hasMultimedia = (Test-Path "$qtDir\bin\Qt6Multimedia.dll") -and (Test-Path "$qtDir\plugins\multimedia\ffmpegmediaplugin.dll") -and
    @(Get-ChildItem "$qtDir\bin\avcodec-*.dll" -ErrorAction SilentlyContinue).Count -gt 0
$pdbProbe = @('Qt6Core.pdb', 'Qt6Quick.pdb', 'Qt6Multimedia.pdb')
$hasPdbs = @($pdbProbe | Where-Object { -not (Test-Path "$qtDir\bin\$_") }).Count -eq 0
$needPdbs = $DebugSymbols -and -not $hasPdbs

if ($hasBase -and $hasShaderTools -and $hasMultimedia -and -not $needPdbs) {
    Write-Host "Qt $QtVersion ($Rid) already present: $qtDir"
} else {
    # aqtinstall needs Python. The Windows Store alias python.exe exists without a real Python, so probe by running it.
    function Find-Python {
        $ErrorActionPreference = 'Continue' # PS 5.1 turns the alias' stderr message into an error record
        foreach ($c in @(@('python'), @('py', '-3'))) {
            $exe = Get-Command $c[0] -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $exe) { continue }
            $cmd = @($exe.Source) + @($c | Select-Object -Skip 1)
            $null = & $cmd[0] @($cmd | Select-Object -Skip 1) -c 'import sys' 2>$null
            if ($LASTEXITCODE -eq 0) { return , $cmd }
        }
    }
    $py = Find-Python
    if (-not $py) {
        $pyHint = 'winget install --id Python.Python.3.13 --scope user'
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw "Python 3 not found (needed for aqtinstall). Install it, e.g.:`n  $pyHint" }
        Write-Host 'Python 3 not found, installing via winget'
        winget install --id Python.Python.3.13 --scope user --silent --accept-package-agreements --accept-source-agreements
        # winget does not update PATH of the running session.
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
        $py = Find-Python
        if (-not $py) { throw "Python 3 still not found after winget install. Open a new shell and retry, or install manually:`n  $pyHint" }
    }
    function python { & $py[0] @($py | Select-Object -Skip 1) @args }
    python -m pip install --quiet --upgrade aqtinstall
    if ($LASTEXITCODE) { throw 'pip install aqtinstall failed' }

    # Fetches 7z archives directly from the Qt repository (fallback when aqt fails, see below).
    function Install-Archives($wanted) {
        $base = "https://download.qt.io/online/qtsdkrepository/windows_x86/desktop/qt6_6120/$($t.Repo)"
        $xml = [xml](Invoke-WebRequest "$base/Updates.xml" -UseBasicParsing).Content
        $tmp = Join-Path $env:TEMP 'qt-dl'
        New-Item -ItemType Directory -Force $tmp, $qtDir | Out-Null
        foreach ($w in $wanted) {
            if ($w.Skip) { continue }
            $u = $xml.Updates.PackageUpdate | Where-Object Name -eq $w.Pkg
            if (-not $u) { throw "package $($w.Pkg) not found in $base/Updates.xml" }
            foreach ($a in ($u.DownloadableArchives -split ',\s*')) {
                if ($a -notmatch $w.Filter) { continue }
                $f = Join-Path $tmp $a
                Invoke-WebRequest "$base/$($w.Pkg)/$($u.Version)$a" -OutFile $f -UseBasicParsing
                python -m py7zr x $f $qtDir
                if ($LASTEXITCODE) { throw "extract $a failed" }
                Remove-Item $f
            }
        }
    }
}

if (-not ($hasBase -and $hasShaderTools -and $hasMultimedia)) {
    # Base install contains qtbase and qtdeclarative (QtQuick, QtQuick.Shapes); -m adds Qt Multimedia and qtshadertools (qsb).
    python -m aqt install-qt $t.Host desktop $QtVersion $t.Arch -O $OutDir -m qtmultimedia qtshadertools
    if ($LASTEXITCODE) {
        if (-not $t.Repo) { throw 'aqt install-qt failed' }
        # aqtinstall 3.3.0 does not know the per-arch repository layout of Qt 6.12 on windows_x86
        # (qt6_6120/qt6_6120_<arch>/ instead of qt6_6120/qt6_6120/). Fetch the 7z archives directly.
        Write-Host "aqt failed, falling back to direct download from $($t.Repo)"
        # Package + archive filter: only what VL.Qt needs; docs, translations, debug symbols and the software
        # GL/d3d redistributables are skipped.
        Install-Archives @(
            @{ Pkg = "qt.qt6.6120.$($t.Arch)";                  Filter = '^(qtbase|qtdeclarative|qtsvg|qttools)-'; Skip = $hasBase }
            @{ Pkg = "qt.qt6.6120.addons.qtshadertools.$($t.Arch)"; Filter = '^qtshadertools-(?!.*debug-symbols)'; Skip = $hasShaderTools }
            @{ Pkg = "qt.qt6.6120.addons.qtmultimedia.$($t.Arch)"; Filter = '^qtmultimedia-(?!.*debug-symbols)'; Skip = $hasMultimedia }
        )
    }
}

if ($needPdbs) {
    # Debug symbols only for what VL.Qt loads: qtbase, qtdeclarative (debug_info) and qtmultimedia.
    # --archives limits base and debug_info to these two, but also filters out the qtmultimedia symbols, and aqt
    # cannot combine it with --noarchives (so the base archives of both are fetched again, identical files).
    # Hence two calls.
    python -m aqt install-qt $t.Host desktop $QtVersion $t.Arch -O $OutDir -m debug_info --archives qtbase qtdeclarative
    if (-not $LASTEXITCODE) {
        python -m aqt install-qt $t.Host desktop $QtVersion $t.Arch -O $OutDir --noarchives -m qtmultimedia.debug_information
    }
    if ($LASTEXITCODE) {
        if (-not $t.Repo) { throw 'aqt install-qt (debug symbols) failed' }
        Write-Host "aqt failed, falling back to direct download of the debug symbols from $($t.Repo)"
        Install-Archives @(
            @{ Pkg = "qt.qt6.6120.debug_info.$($t.Arch)"; Filter = '^(qtbase|qtdeclarative)-.*debug-symbols' }
            @{ Pkg = "qt.qt6.6120.addons.qtmultimedia.debug_information.$($t.Arch)"; Filter = '^qtmultimedia-.*debug-symbols' }
        )
    }
}

$v = & "$qtDir\bin\qtpaths6.exe" --qt-version
if ($v -ne $QtVersion) { throw "qtpaths6 reports $v, expected $QtVersion" }
if (-not (Test-Path "$qtDir\qml\QtQuick\Shapes")) { throw 'QtQuick.Shapes missing' }
if (-not (Test-Path "$qtDir\bin\qsb.exe")) { throw 'qsb missing (qtshadertools)' }
if (-not (Test-Path "$qtDir\qml\QtMultimedia\qmldir")) { throw 'QtMultimedia missing' }
if (-not (Test-Path "$qtDir\plugins\multimedia\ffmpegmediaplugin.dll")) { throw 'Qt Multimedia FFmpeg plugin missing' }
if (-not @(Get-ChildItem "$qtDir\bin\avcodec-*.dll" -ErrorAction SilentlyContinue).Count) { throw 'FFmpeg DLLs (avcodec-*.dll) missing in bin' }
if ($DebugSymbols) {
    $missing = @($pdbProbe | Where-Object { -not (Test-Path "$qtDir\bin\$_") })
    if ($missing) { throw "Qt debug symbols missing in bin: $($missing -join ', ')" }
}
Write-Host "Qt $v OK: $qtDir"
Write-Host "For the CMake presets (dev-win-*): VLQT_QT_DIR=$qtDir"
