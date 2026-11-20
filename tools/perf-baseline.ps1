# Perf baseline of the SmokeTest perf scenarios (issue #169, A4/P8): runs them N times, each in its own `dotnet test` process,
# collects every measured value (Scenario.Perf -> <SmokeDir>/perf.tsv) and writes a CSV (name, unit, run1..runN, median, min, max,
# spread) and a Markdown summary. Compare mode checks a new measurement against a stored baseline CSV.
#
# Usage: tools/perf-baseline.ps1 [-Runs 5] [-SkipNative] [-SkipBuild] [-Rid win-x64] [-Categories a,b]
#                                [-Csv <file>] [-Markdown <file>]
#                                [-Compare <baseline.csv>] [-LimitPercent 3]
#   Without -Compare: writes <Csv> (default build/perf-baseline/perf-baseline.csv) and <Markdown> (same folder).
#   With -Compare: measures (-Runs, default 5; 1 is allowed for a quick check) and compares the medians with the baseline:
#                  limit per value = max(LimitPercent % of the baseline median, spread of the baseline runs);
#                  a value that got slower by more than its limit is flagged ("REGRESSION"), exit code 1. Faster is only reported.
#   -SkipNative  build.ps1 without QtNative (C# only); -SkipBuild: no build at all (the DLLs of the last build are used).
# Conditions: warn-only perf limits (VLQT_PERF_WARN_ONLY=1, a failing limit must not stop the measurement), CI is cleared so the
# heavy measurements run. Run on a quiet machine, nothing else in parallel, and the same machine for before and after: the CPU load
# before each run is recorded. Perf scenarios: see $Categories. Which numbers are "apply time" (time to apply one command frame
# in QtNative): the names containing "apply" (and the 10000-SET/1000-SET apply measurements); writer/reconcile/poll/pump/first-frame
# numbers are other timings. The table in docs/plans/refactor/perf-baseline.md lists them.
param(
    [ValidateRange(1, 50)] [int]$Runs = 5,
    [ValidateSet('win-arm64', 'win-x64')] [string]$Rid,
    [string[]]$Categories = @('core-perf', 'apply', 'reconciler', 'rectangle', 'elements', 'measure', 'pump', 'image-share'),
    [switch]$SkipNative,
    [switch]$SkipBuild,
    [string]$Csv,
    [string]$Markdown,
    [string]$Compare,
    [double]$LimitPercent = 3,
    [ValidateRange(10, 86400)] [int]$Timeout = 900
)
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture

$repo = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Rid) { $Rid = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'win-arm64' } else { 'win-x64' } }
$work = "$repo\build\perf-baseline"
if (-not $Csv) { $Csv = "$work\perf-baseline.csv" }
if (-not $Markdown) { $Markdown = [IO.Path]::ChangeExtension($Csv, '.md') }
if ($Compare -and -not (Test-Path $Compare)) { throw "Baseline not found: $Compare" }

function Get-CpuLoad
{
    # Average of 5 samples over 5 s of the total CPU load (percent).
    $s = 1..5 | ForEach-Object { (Get-Counter '\Processor Information(_Total)\% Processor Time' -ErrorAction Stop).CounterSamples[0].CookedValue; Start-Sleep -Milliseconds 1000 }
    [math]::Round(($s | Measure-Object -Average).Average, 1)
}
function Get-Median([double[]]$v) { $s = $v | Sort-Object; $n = $s.Count; if ($n % 2) { $s[($n - 1) / 2] } else { ($s[$n / 2 - 1] + $s[$n / 2]) / 2 } }
function Format-Num([double]$x) { $x.ToString('G6', $inv) }

# --- build once ---
if (-not $SkipBuild)
{
    $b = @{ Rid = $Rid }
    if ($SkipNative) { $b.SkipNative = $true }
    & "$PSScriptRoot\build.ps1" @b
    if ($LASTEXITCODE) { throw "build.ps1 failed (exit $LASTEXITCODE)" }
    dotnet build "$repo\src\VL.Qt.SmokeTest" -c Release -nologo -v q "-p:RuntimeIdentifier=$Rid"
    if ($LASTEXITCODE) { throw "SmokeTest build failed (exit $LASTEXITCODE)" }
}

# --- runs ---
$env:VLQT_PERF_WARN_ONLY = '1'
Remove-Item Env:CI -ErrorAction SilentlyContinue
Remove-Item Env:VLQT_PERF_FACTOR -ErrorAction SilentlyContinue
if (Test-Path $work) { Remove-Item "$work\run*" -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $work | Out-Null
$filter = ($Categories | ForEach-Object { "TestCategory=$_" }) -join '|'

$values = [ordered]@{}   # name -> @{ unit; vals = list }
$loads = @()
for ($r = 1; $r -le $Runs; $r++)
{
    $load = Get-CpuLoad
    $loads += $load
    Write-Host "== run $r/$Runs (CPU load before: $load %)" -ForegroundColor Cyan
    $dir = "$work\run$r"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $env:VLQT_SMOKE_SMOKEDIR = $dir
    $testArgs = @("$repo\src\VL.Qt.SmokeTest", '-c', 'Release', '-nologo', '--no-build', "-p:RuntimeIdentifier=$Rid", '--filter', $filter,
        '--logger', 'trx;LogFileName=smoke.trx', '--results-directory', $dir, '--blame-hang', '--blame-hang-timeout', "$($Timeout)s")
    dotnet test @testArgs 2>&1 | Tee-Object "$dir\dotnet-test.log" | ForEach-Object { if ("$_" -match '^\s*(Failed |Passed!|Failed!|Skipped!|error)|No test matches|\[FAIL\]') { Write-Host $_ } }
    $tsv = "$dir\perf.tsv"
    if (-not (Test-Path $tsv)) { throw "run ${r}: no perf.tsv (see $dir\dotnet-test.log)" }
    foreach ($line in Get-Content $tsv)
    {
        $f = $line -split "`t"
        if ($f.Count -lt 4) { continue }
        if (-not $values.Contains($f[1])) { $values[$f[1]] = @{ unit = $f[3]; vals = [Collections.Generic.List[double]]::new() } }
        $values[$f[1]].vals.Add([double]::Parse($f[2], $inv))
    }
    # A scenario that did not run (skipped, failed) leaves its names short: say so.
    foreach ($k in $values.Keys) { if ($values[$k].vals.Count -ne $r) { Write-Warning "run ${r}: '$k' has $($values[$k].vals.Count) of $r values" } }
}
Remove-Item Env:VLQT_SMOKE_SMOKEDIR -ErrorAction SilentlyContinue

$rows = @(foreach ($k in $values.Keys)
{
    $v = $values[$k].vals.ToArray()
    [pscustomobject]@{ Name = $k; Unit = $values[$k].unit; Vals = $v; Median = (Get-Median $v); Min = ($v | Measure-Object -Minimum).Minimum; Max = ($v | Measure-Object -Maximum).Maximum }
})

if (-not $Compare)
{
    # --- CSV + Markdown ---
    New-Item -ItemType Directory -Force (Split-Path $Csv) | Out-Null
    $head = @('name', 'unit') + (1..$Runs | ForEach-Object { "run$_" }) + @('median', 'min', 'max', 'spread')
    $lines = @($head -join ',')
    foreach ($x in $rows)
    {
        $lines += (@($x.Name, $x.Unit) + ($x.Vals | ForEach-Object { Format-Num $_ }) + @((Format-Num $x.Median), (Format-Num $x.Min), (Format-Num $x.Max), (Format-Num ($x.Max - $x.Min)))) -join ','
    }
    [IO.File]::WriteAllText($Csv, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

    $commit = (git -C $repo rev-parse --short HEAD).Trim()
    $dirty = if (git -C $repo status --porcelain) { ' (+ uncommitted changes)' } else { '' }
    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
    $gpu = if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { (nvidia-smi --query-gpu=name,driver_version --format=csv,noheader | Select-Object -First 1).Trim() -replace ',\s*', ', driver ' }
           else { (Get-CimInstance Win32_VideoController | Select-Object -First 1 | ForEach-Object { "$($_.Name), driver $($_.DriverVersion)" }) }
    $qt = (Get-ChildItem C:\Qt -Directory -ErrorAction SilentlyContinue | Where-Object Name -match '^6\.' | Sort-Object Name | Select-Object -Last 1).Name
    $os = (Get-CimInstance Win32_OperatingSystem).Caption + ' ' + (Get-CimInstance Win32_OperatingSystem).Version
    $md = [Text.StringBuilder]::new()
    [void]$md.AppendLine('# Perf baseline of the SmokeTest perf scenarios')
    [void]$md.AppendLine()
    [void]$md.AppendLine('Generated by `tools/perf-baseline.ps1` (do not edit by hand). Raw values: [perf-baseline.csv](perf-baseline.csv).')
    [void]$md.AppendLine()
    [void]$md.AppendLine('| | |')
    [void]$md.AppendLine('|---|---|')
    [void]$md.AppendLine("| Date | $(Get-Date -Format 'yyyy-MM-dd HH:mm') |")
    [void]$md.AppendLine("| Commit | $commit$dirty |")
    [void]$md.AppendLine("| Machine | $env:COMPUTERNAME, $os, $Rid |")
    [void]$md.AppendLine("| CPU | $cpu |")
    [void]$md.AppendLine("| GPU | $gpu |")
    [void]$md.AppendLine("| Qt | $qt |")
    [void]$md.AppendLine("| Runs | $Runs, one ``dotnet test`` process each, scenarios: $($Categories -join ', ') |")
    [void]$md.AppendLine("| CPU load before each run (5 s mean, %) | $(($loads | ForEach-Object { Format-Num $_ }) -join ', ') |")
    [void]$md.AppendLine('| Conditions | Release, `VLQT_PERF_WARN_ONLY=1`, `CI` unset (heavy measurements run), nothing else started by the job |')
    [void]$md.AppendLine()
    [void]$md.AppendLine('Limit for the before/after comparison (wave 3, u32 length): a value is flagged when its median is slower by more than max(3 % of the baseline median, spread of the baseline runs).')
    [void]$md.AppendLine()
    foreach ($grp in @(@('Apply time (one command frame applied in QtNative)', { $_.Name -match 'apply' }), @('Other timings', { $_.Name -notmatch 'apply' })))
    {
        [void]$md.AppendLine("## $($grp[0])")
        [void]$md.AppendLine()
        [void]$md.AppendLine('| Name | Unit | Median | Min | Max | Spread | Spread % of median | Limit (3 % / spread) |')
        [void]$md.AppendLine('|---|---|---:|---:|---:|---:|---:|---:|')
        foreach ($x in ($rows | Where-Object $grp[1]))
        {
            $sp = $x.Max - $x.Min
            $pct = if ($x.Median) { 100 * $sp / $x.Median } else { 0 }
            $lim = [math]::Max($LimitPercent / 100 * $x.Median, $sp)
            [void]$md.AppendLine("| ``$($x.Name)`` | $($x.Unit) | $(Format-Num $x.Median) | $(Format-Num $x.Min) | $(Format-Num $x.Max) | $(Format-Num $sp) | $(Format-Num $pct) | $(Format-Num $lim) |")
        }
        [void]$md.AppendLine()
    }
    [IO.File]::WriteAllText($Markdown, $md.ToString(), [Text.UTF8Encoding]::new($false))
    Write-Host "Wrote $Csv and $Markdown" -ForegroundColor Green
    exit 0
}

# --- compare mode ---
$base = @{}
foreach ($o in (Import-Csv $Compare)) { $base[$o.name] = $o }
$out = [Text.StringBuilder]::new()
[void]$out.AppendLine("# Perf comparison against $(Split-Path $Compare -Leaf)")
[void]$out.AppendLine()
[void]$out.AppendLine("Runs: $Runs, CPU load before each run (%): $(($loads | ForEach-Object { Format-Num $_ }) -join ', '). Limit per value: max($LimitPercent % of the baseline median, spread of the baseline runs); flagged only when slower.")
[void]$out.AppendLine()
[void]$out.AppendLine('| Name | Unit | Baseline median | Baseline spread | New median | New spread | Diff | Diff % | Limit | Result |')
[void]$out.AppendLine('|---|---|---:|---:|---:|---:|---:|---:|---:|---|')
$bad = 0
foreach ($x in $rows)
{
    if (-not $base.ContainsKey($x.Name)) { [void]$out.AppendLine("| ``$($x.Name)`` | $($x.Unit) | | | $(Format-Num $x.Median) | | | | | not in baseline |"); continue }
    $b = $base[$x.Name]
    $bm = [double]::Parse($b.median, $inv); $bs = [double]::Parse($b.spread, $inv)
    $diff = $x.Median - $bm
    $pct = if ($bm) { 100 * $diff / $bm } else { 0 }
    $lim = [math]::Max($LimitPercent / 100 * $bm, $bs)
    $res = if ($diff -gt $lim) { $bad++; 'REGRESSION' } elseif ($diff -lt -$lim) { 'faster' } else { 'ok' }
    [void]$out.AppendLine("| ``$($x.Name)`` | $($x.Unit) | $(Format-Num $bm) | $(Format-Num $bs) | $(Format-Num $x.Median) | $(Format-Num ($x.Max - $x.Min)) | $(Format-Num $diff) | $(Format-Num $pct) | $(Format-Num $lim) | $res |")
}
foreach ($k in $base.Keys) { if (-not ($rows | Where-Object Name -eq $k)) { [void]$out.AppendLine("| ``$k`` | | | | | | | | | missing in this run |"); $bad++ }}
New-Item -ItemType Directory -Force (Split-Path $Markdown) | Out-Null
$cmp = [IO.Path]::ChangeExtension($Markdown, $null).TrimEnd('.') + '-compare.md'
if ($PSBoundParameters.ContainsKey('Markdown')) { $cmp = $Markdown }
[IO.File]::WriteAllText($cmp, $out.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host $out.ToString()
Write-Host "Wrote $cmp" -ForegroundColor Green
if ($bad) { Write-Host "$bad value(s) beyond the limit or missing" -ForegroundColor Yellow; exit 1 }
Write-Host 'All values within the limits.' -ForegroundColor Green
exit 0
