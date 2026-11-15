# Builds everything needed to test VL.Qt locally: QtNative (+ Qt deploy), the C# library, VL.Qt.Stride and VL.Qt.Audio, optionally tests and a patch.
# Usage: tools/build.ps1 [-Rid win-arm64|win-x64] [-Configuration Release|Debug] [-SkipNative] [-ForceDeploy]
#                        [-Test] [-Only <scenario,...>] [-SmokeTimeout <s>] [-Patch <doc.vl>]
#   -Test   unit tests (VL.Qt.Tests) + SmokeTest (log and grabs in build/smoke/); -Only passes --only to the SmokeTest.
#           Without -Only, QtTexture also runs in both thread modes (render thread, VLQT_OFFSCREEN_THREAD=0), grabs in build/smoke/qttexture-*
#   -SmokeTimeout  seconds before the SmokeTest is killed (default 600; a full run takes about 4 min on win-arm64)
#   -Patch  compile-checks a patch with vvvvc (tools/vvvvc.ps1), e.g. -Patch "help/Qt/HowTo Draw a Rectangle.vl"
# Rid defaults to the host architecture (only the Qt of that architecture is installed locally).
# Exit code 0 = all requested steps passed; the summary at the end lists each step.
param(
    [ValidateSet('win-arm64', 'win-x64')] [string]$Rid,
    [ValidateSet('Release', 'Debug')] [string]$Configuration = 'Release',
    [switch]$SkipNative,
    [switch]$ForceDeploy,
    [switch]$Test,
    [string]$Only,
    [ValidateRange(1, 86400)] [int]$SmokeTimeout = 600,
    [string]$Patch
)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Rid) { $Rid = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'win-arm64' } else { 'win-x64' } }

$results = [ordered]@{}
# Steps after a failed one are skipped (testing against a stale QtNative or DLL would mislead).
function Invoke-Step([string]$name, [scriptblock]$body)
{
    if ($results.Values -like 'FAIL*') { $results[$name] = 'SKIP'; return }
    Write-Host "== $name" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try
    {
        & $body
        $results[$name] = 'PASS ({0:N0} s)' -f $sw.Elapsed.TotalSeconds
    }
    catch
    {
        $results[$name] = "FAIL: $($_.Exception.Message)"
        Write-Host "FAIL: $($_.Exception.Message)" -ForegroundColor Red
    }
}
function Assert-Exit([string]$what) { if ($LASTEXITCODE) { throw "$what failed (exit $LASTEXITCODE)" } }

if (-not $SkipNative)
{
    # Ninja/windeployqt print hundreds of lines: show compiler errors/warnings and the deploy summary, the tail on failure.
    Invoke-Step "native ($Rid)" {
        $lines = [Collections.Generic.List[string]]::new()
        # Windows PowerShell 5.1 wraps native stderr lines in error records; empty ones print as the exception type name.
        try { & "$PSScriptRoot\build-native.ps1" -Rid $Rid -ForceDeploy:$ForceDeploy *>&1 | ForEach-Object { $lines.Add(("$_" -replace '^System\.Management\.Automation\.RemoteException$', '')) } }
        catch { $lines | Select-Object -Last 60 | Write-Host; throw }
        $lines -match ': (fatal )?(error|warning) |^Deployed to |^Forcing ' | Write-Host
    }
}

Invoke-Step "VL.Qt ($Configuration)" {
    dotnet build "$repo\src\VL.Qt" -c $Configuration -v q -nologo
    Assert-Exit 'dotnet build'
}

# Windows only (VL.Stride); the output lands in VL.Qt.Stride\lib\net10.0-windows7.0 (package folder, git-ignored).
Invoke-Step "VL.Qt.Stride ($Configuration)" {
    dotnet build "$repo\src\VL.Qt.Stride" -c $Configuration -v q -nologo
    Assert-Exit 'dotnet build VL.Qt.Stride'
}

# Windows only (VL.Audio); the output lands in VL.Qt.Audio\lib\net10.0-windows7.0 (package folder, git-ignored).
Invoke-Step "VL.Qt.Audio ($Configuration)" {
    dotnet build "$repo\src\VL.Qt.Audio" -c $Configuration -v q -nologo
    Assert-Exit 'dotnet build VL.Qt.Audio'
}

if ($Test)
{
    Invoke-Step 'unit tests' {
        dotnet test "$repo\src\VL.Qt.Tests" -c $Configuration -nologo --logger "console;verbosity=normal" --logger "trx;LogFileName=unit.trx" --results-directory "$repo\build\test-results"
        Assert-Exit 'dotnet test'
    }
    # One SmokeTest process; stdout.log and grabs go to $out. $threads sets VLQT_OFFSCREEN_THREAD for it (empty = unset: default).
    function Invoke-Smoke([string]$exe, [string]$out, [string[]]$extraArgs, [string]$threads, [string]$modeLine)
    {
        New-Item -ItemType Directory -Force $out | Out-Null
        $smokeArgs = @("`"$out\grabs`"") + $extraArgs
        $saved = $env:VLQT_OFFSCREEN_THREAD
        if ($threads) { $env:VLQT_OFFSCREEN_THREAD = $threads }
        try
        {
            $p = Start-Process $exe -ArgumentList $smokeArgs -NoNewWindow -PassThru `
                -RedirectStandardOutput "$out\stdout.log" -RedirectStandardError "$out\stderr.log"
        }
        finally { $env:VLQT_OFFSCREEN_THREAD = $saved }
        $null = $p.Handle # without the cached handle ExitCode stays empty (Windows PowerShell): a crash counted as PASS (#534)
        $sw = [Diagnostics.Stopwatch]::StartNew()
        if (-not $p.WaitForExit($SmokeTimeout * 1000))
        {
            $p.Kill($true)
            $p.WaitForExit()
            # The last lines show the scenario that was running when the limit hit.
            Get-Content "$out\stdout.log" -Tail 5 | Write-Host
            throw ('timeout after {0:N0} s (-SmokeTimeout {1}), see {2}\stdout.log' -f $sw.Elapsed.TotalSeconds, $SmokeTimeout, $out)
        }
        $p.WaitForExit()
        $log = @(Get-Content "$out\stdout.log")
        $log -match '^(FAIL|WARN|SMOKE) ' | Write-Host
        Write-Host ('{0} PASS, {1} FAIL, log + grabs: {2}' -f @($log -match '^PASS ').Count, @($log -match '^FAIL ').Count, $out)
        if ($p.ExitCode) { throw "exit $($p.ExitCode), see $out\stdout.log" }
        # the log is the second witness: an empty ExitCode must not turn a red run into PASS
        if (-not ($log -contains 'SMOKE OK')) { throw "no 'SMOKE OK' in $out\stdout.log" }
        # the run really used the mode it is named for (QtNative logs it at the first offscreen scene)
        if ($modeLine -and -not ($log -match [regex]::Escape($modeLine))) { throw "no '$modeLine' in $out\stdout.log: wrong thread mode" }
    }

    $exe = $null
    Invoke-Step "SmokeTest ($Rid)" {
        dotnet build "$repo\src\VL.Qt.SmokeTest" -c $Configuration -v q -nologo -p:RuntimeIdentifier=$Rid
        Assert-Exit 'SmokeTest build'
        $script:exe = (Get-ChildItem "$repo\src\VL.Qt.SmokeTest\bin\$Configuration" -Recurse -Filter VL.Qt.SmokeTest.exe |
            Where-Object FullName -like "*\$Rid\*" | Select-Object -First 1).FullName
        if (-not $exe) { throw "VL.Qt.SmokeTest.exe ($Rid) not found" }
        $out = "$repo\build\smoke"
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        Invoke-Smoke $exe $out $(if ($Only) { '--only', $Only }) '' ''
    }
    # QtTexture renders offscreen scenes on a Qt render thread by default or on the main thread (VLQT_OFFSCREEN_THREAD=0, #116):
    # its suites run in both modes, as processes of their own (the qttexture device loss parts need D3D11On12 from the start,
    # which the full run does not set). Not with -Only: that is a narrowed run.
    if (-not $Only)
    {
        foreach ($mode in @(@{ Name = 'render thread'; Threads = ''; Line = 'offscreen scenes render on 1 Qt render thread(s)' },
                            @{ Name = 'main thread'; Threads = '0'; Line = 'offscreen scenes render on the main thread' }))
        {
            Invoke-Step "QtTexture ($($mode.Name))" {
                Invoke-Smoke $exe ("$repo\build\smoke\qttexture-" + ($mode.Name -replace ' ', '-')) @('--only', 'qttexture,QtTextureInput') $mode.Threads $mode.Line
            }
        }
    }
}

if ($Patch)
{
    Invoke-Step "vvvvc $(Split-Path $Patch -Leaf)" {
        $file = if (Test-Path (Join-Path $repo $Patch)) { Join-Path $repo $Patch } else { $Patch }
        & "$PSScriptRoot\vvvvc.ps1" -File (Resolve-Path $file).Path
        Assert-Exit 'vvvvc'
    }
}

Write-Host ''
foreach ($r in $results.GetEnumerator())
{
    $color = switch -Wildcard ($r.Value) { 'PASS*' { 'Green' } 'FAIL*' { 'Red' } default { 'Yellow' } }
    Write-Host ('{0,-28} {1}' -f $r.Key, $r.Value) -ForegroundColor $color
}
exit [int][bool]($results.Values -like 'FAIL*')
