# Builds everything needed to test VL.Qt locally: QtNative (+ Qt deploy), the C# library, VL.Qt.Stride and VL.Qt.Audio, optionally tests and a patch.
# Usage: tools/build.ps1 [-Rid win-arm64|win-x64] [-Configuration Release|Debug] [-SkipNative] [-ForceDeploy]
#                        [-Test] [-Only <scenario,...>] [-SmokeTimeout <s>] [-Patch <doc.vl>]
#   -Test   unit tests (VL.Qt.Tests, VL.Qt.Audio.Tests) + SmokeTest via `dotnet test src/VL.Qt.SmokeTest` (build/smoke/: smoke.trx,
#           checks.tsv, smoke.log, grabs/); -Only a,b = NUnit filter TestCategory=a|TestCategory=b (the scenario names).
#           The QtTexture thread modes, device-lost etc. are own-process tests inside the run (no separate steps).
#   -SmokeTimeout  seconds: hang timeout of the test host and ScenarioTimeoutSeconds of the SmokeTest (default 600)
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
    Invoke-Step 'audio unit tests' {
        dotnet test "$repo\src\VL.Qt.Audio.Tests" -c $Configuration -nologo --logger "console;verbosity=normal" --logger "trx;LogFileName=audio.trx" --results-directory "$repo\build\test-results"
        Assert-Exit 'dotnet test VL.Qt.Audio.Tests'
    }
    # The SmokeTest is an NUnit project (one Qt start for the run; qttexture-render-thread / qttexture-main-thread, device-lost
    # and the other own-process scenarios run as helper processes of their own). Results: build/smoke/smoke.trx, grabs in
    # build/smoke/grabs, Qt log smoke.log, one line per check in checks.tsv.
    Invoke-Step "SmokeTest ($Rid)" {
        $out = "$repo\build\smoke"
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        New-Item -ItemType Directory -Force $out | Out-Null
        $testArgs = @("$repo\src\VL.Qt.SmokeTest", '-c', $Configuration, '-nologo', "-p:RuntimeIdentifier=$Rid",
            '--logger', 'trx;LogFileName=smoke.trx', '--results-directory', $out,
            '--blame-hang', '--blame-hang-timeout', "$($SmokeTimeout)s")
        # -Only a,b: the scenarios are the TestCategory values
        if ($Only) { $testArgs += '--filter', (($Only -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { "TestCategory=$_" }) -join '|') }
        # run settings: a scenario that hangs inside Qt counts as blocked after -SmokeTimeout
        $testArgs += '--', "TestRunParameters.Parameter(name=`"ScenarioTimeoutSeconds`", value=`"$SmokeTimeout`")"
        dotnet test @testArgs 2>&1 | Tee-Object "$out\dotnet-test.log" | ForEach-Object { if ("$_" -match '^\s*(Failed |Passed!|Failed!|Skipped!|error|warning)|No test matches|\[FAIL\]') { Write-Host $_ } }
        $exit = $LASTEXITCODE
        $trx = "$out\smoke.trx"
        $counters = $null
        if (Test-Path $trx) { $counters = ([xml](Get-Content $trx -Raw)).TestRun.ResultSummary.Counters }
        # Failing tests with their message, then the failing check lines.
        if (Test-Path $trx)
        {
            $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
            foreach ($r in (Select-Xml -Path $trx -XPath '//t:UnitTestResult[@outcome="Failed"]' -Namespace $ns).Node)
            {
                Write-Host "FAILED TEST $($r.testName)" -ForegroundColor Red
                $msg = $r.Output.ErrorInfo.Message
                if ($msg) { ($msg -split "`n" | Select-Object -First 8) | ForEach-Object { Write-Host "    $_" -ForegroundColor Red } }
            }
        }
        if (Test-Path "$out\checks.tsv")
        {
            $bad = @(Get-Content "$out\checks.tsv" | Where-Object { ($_ -split "`t")[1] -eq 'FAIL' })
            foreach ($l in $bad) { Write-Host ('FAIL check: ' + ($l -replace "`t", ' | ')) -ForegroundColor Red }
        }
        if ($counters) { Write-Host ('{0} passed, {1} failed, {2} not executed of {3} tests; trx + grabs + logs: {4}' -f $counters.passed, $counters.failed, $counters.notExecuted, $counters.total, $out) }
        if ($exit) { throw "dotnet test exit $exit, see $out\dotnet-test.log" }
        # an unknown -Only name selects nothing, and dotnet test would still exit 0
        if (-not $counters -or [int]$counters.executed -eq 0) { throw "no SmokeTest ran (filter: $Only), see $out\dotnet-test.log" }
        if ([int]$counters.failed -gt 0) { throw "$($counters.failed) tests failed, see $trx" }
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
