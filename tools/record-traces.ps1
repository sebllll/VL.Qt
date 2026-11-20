# Records command traces (VLQT_TRACE_COMMANDS) from help patches and stores them in src/VL.Qt.SmokeTest/traces/<name>.bin.
# The SmokeTest replays every trace against QtNative and compares the last frame with references/trace-<name>.png (TraceReplay,
# category replay-traces). Every patch here is static: a patch with LFOs records wall-clock values and would not replay to the
# same reference after a re-record (the point of the traces). If an animated one is ever unavoidable, mark it here as
# "animated: re-record needs a new reference, compare by eye". A trace is bound to the wire format: re-record after every
# protocol change; the references must stay the same (if they must change on purpose:
# `dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=update-refs`).
#   tools\record-traces.ps1                       # all traces
#   tools\record-traces.ps1 -Only rectangle,text  # some
#   tools\record-traces.ps1 -Frames 120           # frames per trace (default 90)
# Needs a built VL.Qt (tools/build.ps1 or `dotnet build src/VL.Qt -c Release`). vvvv: -VvvvDir, else $env:VVVV_DIR, else the newest C:\vvvv\vvvv_gamma_*-win-<arch>.
# Per trace: starts vvvv with the patch, waits until -Frames frames are written (a static patch writes few frames: until no new
# frame comes for 10 s), stops vvvv, cuts the trace to at most -Frames frames and copies it. A trace that needs image files gets a text file
# <name>.images next to it (one path relative to the repository per line, in the order of the resource ids 1, 2, ...).
param(
    [string[]]$Only,
    [int]$Frames = 90,
    [int]$TimeoutSeconds = 120,
    [string]$RepoDir = (Split-Path $PSScriptRoot -Parent),
    [string]$VvvvDir = $env:VVVV_DIR
)
$ErrorActionPreference = 'Stop'
$RepoDir = (Resolve-Path $RepoDir).Path
$arch = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'arm64' } else { 'x64' }

# name -> help patch (+ image files the patch loads)
$traces = [ordered]@{
    'rectangle' = @{ Patch = 'HowTo Antialias Edges' }
    'text'      = @{ Patch = 'HowTo Draw Text' }
    'shape'     = @{ Patch = 'HowTo Draw a Polygon' }
    'layout'    = @{ Patch = 'HowTo Layout with Row and Column' }
    'widgets'   = @{ Patch = 'HowTo Use a Slider' }
    'image'     = @{ Patch = 'HowTo Use an IImage'; Images = @('help/Qt/assets/qt-help-image.png') }
    'effects'   = @{ Patch = 'HowTo Chain Effects' }
    'qml'       = @{ Patch = 'HowTo Use a QML file' }
}
if ($Only) { foreach ($n in $Only) { if (-not $traces.Contains($n)) { throw "unknown trace '$n' (known: $($traces.Keys -join ', '))" } } }

if (-not $VvvvDir) {
    $VvvvDir = Get-ChildItem 'C:\vvvv' -Directory -Filter "vvvv_gamma_*-win-$arch" |
        Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $VvvvDir) { throw "no vvvv_gamma_*-win-$arch under C:\vvvv; pass -VvvvDir" }
}
$pkgRepo = (& (Join-Path $PSScriptRoot 'start-vvvv.ps1') -PrepareOnly -RepoDir $RepoDir) | Select-Object -Last 1
$outDir = Join-Path $RepoDir 'src\VL.Qt.SmokeTest\traces'
New-Item -ItemType Directory -Force $outDir | Out-Null

# Frames (u32 length + bytes) of a trace file that is still being written; stops at an incomplete frame.
function Read-Frames([string]$path) {
    $list = [Collections.Generic.List[byte[]]]::new()
    $bytes = try {
        $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        try { $ms = [IO.MemoryStream]::new(); $fs.CopyTo($ms); $ms.ToArray() } finally { $fs.Dispose() }
    } catch { return , $list }
    $pos = 0
    while ($pos + 4 -le $bytes.Length) {
        $len = [BitConverter]::ToUInt32($bytes, $pos)
        if ($pos + 4 + $len -gt $bytes.Length) { break }
        $list.Add([byte[]]$bytes[($pos + 4)..($pos + 3 + $len)]) | Out-Null
        $pos += 4 + $len
    }
    , $list
}

foreach ($name in $traces.Keys) {
    if ($Only -and $name -notin $Only) { continue }
    $info = $traces[$name]
    $patch = Get-ChildItem (Join-Path $RepoDir 'help') -Recurse -Filter "$($info.Patch).vl" | Select-Object -First 1 -ExpandProperty FullName
    if (-not $patch) { throw "help patch '$($info.Patch)' not found" }
    Write-Host "== $name ($($info.Patch))" -ForegroundColor Cyan

    $env:VLQT_TRACE_COMMANDS = '1'
    # The patch path must come before --package-repositories.
    $p = Start-Process (Join-Path $VvvvDir 'vvvv.exe') -ArgumentList "`"$patch`" --package-repositories `"$pkgRepo`"" -PassThru
    Remove-Item Env:VLQT_TRACE_COMMANDS
    $trace = Join-Path ([IO.Path]::GetTempPath()) "vlqt-trace-$($p.Id).bin"
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $got = $null
        $lastCount = 0
        $stableSince = [DateTime]::UtcNow
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 1000
            if ($p.HasExited) { throw "vvvv ended early (exit code $($p.ExitCode))" }
            if (Test-Path $trace) {
                $got = Read-Frames $trace
                if ($got.Count -ge $Frames) { break }
                # A static patch writes few frames (a frame only when something changes): done when no new frame comes for 10 s.
                if ($got.Count -ne $lastCount) { $lastCount = $got.Count; $stableSince = [DateTime]::UtcNow }
                elseif ($got.Count -gt 0 -and ([DateTime]::UtcNow - $stableSince).TotalSeconds -ge 10) { break }
            }
        }
        if (-not $got -or $got.Count -lt 1) { throw "no frame after $TimeoutSeconds s ($trace)" }
        $count = [Math]::Min($got.Count, $Frames)
    }
    finally {
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit() }
    }

    $target = Join-Path $outDir "$name.bin"
    $fs = [IO.File]::Create($target)
    try {
        for ($i = 0; $i -lt $count; $i++) {
            $fs.Write([BitConverter]::GetBytes([uint32]$got[$i].Length), 0, 4)
            $fs.Write($got[$i], 0, $got[$i].Length)
        }
    }
    finally { $fs.Dispose() }
    Remove-Item $trace -ErrorAction SilentlyContinue
    if ($info.Images) { Set-Content (Join-Path $outDir "$name.images") $info.Images }
    Write-Host ("   {0}: {1} frames, {2:N0} bytes" -f "$name.bin", $count, (Get-Item $target).Length)
}
