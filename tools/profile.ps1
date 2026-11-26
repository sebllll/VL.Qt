# CPU profiling baseline with Ultra (issue #557). Contract and scenarios: tools/profiling/README.md.
# Usage: tools/profile.ps1 [-Scenario <Name[]>|all] [-Out <dir>] [-WarmUp 10] [-Duration 30] [-NoBuild] [-DryRun]
#   Scenario names: Idle, Elements500, Elements2000, Typo, Images, Video, Effects (default: all).
#   Out defaults to %LOCALAPPDATA%\VL.Qt\profiling\<yyMMdd_HHmm>_<rid>\ (outside the repo, profiles are tens of MB).
# Steps: (1) dotnet build src/VL.Qt -c Release and vvvvc export of the patches to build/profiling/<Patch>/,
#        (2) ONE elevated pwsh (one UAC prompt) records all scenarios in turn with `ultra profile`,
#        (3) `ultra report` per profile -> <Out>/<Scenario>.report.txt, (4) <Out>/manifest.json.
# -DryRun does (1), writes plan.json + manifest.json and prints the ultra command lines; no elevation, no capture.
# Internal: -CaptureOnly/-PlanFile (the elevated part), -PatchOverride (use this .vl for every scenario, for testing).
# The elevated part logs to <Out>/capture.log and passes its exit code back (non-zero if a scenario failed).
#Requires -Version 7
[CmdletBinding(PositionalBinding = $false)]
param(
    [string[]]$Scenario = @('all'),
    [string]$Out,
    [int]$WarmUp = 10,
    [int]$Duration = 30,
    [switch]$NoBuild,
    [switch]$DryRun,
    [switch]$CaptureOnly,
    [string]$PlanFile,
    [string]$PatchOverride
)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path "$PSScriptRoot\..").Path
$hostArch = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'arm64' } else { 'x64' }
$rid = "win-$hostArch"

# ---------------------------------------------------------------------------------------------------------------
# Elevated part: reads the plan, records every scenario, writes <Out>/capture.log. Exit code 0 = all captured.
# ---------------------------------------------------------------------------------------------------------------
if ($CaptureOnly) {
    $plan = Get-Content -Raw $PlanFile | ConvertFrom-Json
    $log = Join-Path $plan.out 'capture.log'
    function Write-Log([string]$msg) {
        $line = "$(Get-Date -Format 'HH:mm:ss') $msg"
        Write-Host $line
        Add-Content -Path $log -Value $line -Encoding utf8NoBOM
    }
    Write-Log "capture start: $($plan.scenarios.Count) scenario(s), warm-up $($plan.warmUp) s, duration $($plan.duration) s"
    $env:_NT_SYMBOL_PATH = $plan.symbolPath
    $failed = 0
    foreach ($s in $plan.scenarios) {
        Write-Log "== $($s.name): $($s.exe)"
        $saved = @{}
        foreach ($p in $s.env.PSObject.Properties) {
            $saved[$p.Name] = [Environment]::GetEnvironmentVariable($p.Name)
            [Environment]::SetEnvironmentVariable($p.Name, [string]$p.Value)
        }
        $result = Join-Path $plan.out "$($s.name).result.json"
        Remove-Item $result -ErrorAction SilentlyContinue
        # Start the program ourselves (normal visible window, assets relative to the exe), warm up, then attach.
        # Launched by ultra ('-- <exe>') the window was never exposed: no scenegraph render in the profiles.
        $code = -1
        $app = Start-Process $s.exe -WorkingDirectory (Split-Path $s.exe) -WindowStyle Normal -PassThru
        Start-Sleep -Seconds $plan.warmUp
        if ($app.HasExited) { Write-Log "program exited during warm-up (code $($app.ExitCode))" }
        else {
            $ultraArgs = @('--yes', '--', 'ultra', 'profile', '--pid', $app.Id, '--duration', $plan.duration,
                '--output', $s.output, '--result-file', $result, '--symbol-path', $plan.symbolPath)
            Write-Log "dnx $($ultraArgs -join ' ')"
            & dnx @ultraArgs 2>&1 | ForEach-Object { Write-Log "$_" }
            $code = $LASTEXITCODE
        }
        # Ultra stops after the duration; the exported program may keep running: end it.
        foreach ($proc in Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $s.exe }) {
            Write-Log "stopping leftover process $($proc.Id)"
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
        # Ultra writes the result file on failure too: success and the profile list decide, not only the exit code.
        $ok = $false
        if (Test-Path $result) {
            $r = Get-Content -Raw $result | ConvertFrom-Json
            $ok = [bool]$r.success -and $r.profiles
            if (-not $ok) { Write-Log "ultra error: $($r.error)" }
        }
        if ($code -ne 0 -or -not $ok) { $failed++; Write-Log "FAILED $($s.name) (ultra exit $code)" }
        else { Write-Log "ok $($s.name): $(($r.profiles | ForEach-Object { $_.path }) -join ', ')" }
    }
    Write-Log "capture done, failed: $failed"
    exit ([int]($failed -gt 0))
}

# ---------------------------------------------------------------------------------------------------------------
# Normal run
# ---------------------------------------------------------------------------------------------------------------
$scenarioDefs = [ordered]@{
    Idle         = @{ Patch = 'Profile01_Idle';     Env = @{} }
    Elements500  = @{ Patch = 'Profile02_Elements'; Env = @{ VLQT_PROFILE_COUNT = 500 };  Variant = 'Profile02_Elements500' }
    Elements2000 = @{ Patch = 'Profile02_Elements'; Env = @{ VLQT_PROFILE_COUNT = 2000 }; Variant = 'Profile02_Elements2000' }
    Typo         = @{ Patch = 'Profile03_Typo';     Env = @{} }
    Images       = @{ Patch = 'Profile04_Images';   Env = @{} }
    Video        = @{ Patch = 'Profile05_Video';    Env = @{} }
    Effects      = @{ Patch = 'Profile06_Effects';  Env = @{} }
}
$names = if ($Scenario -contains 'all') { @($scenarioDefs.Keys) } else { $Scenario }
foreach ($n in $names) {
    if (-not $scenarioDefs.Contains($n)) { throw "unknown scenario '$n' (valid: $($scenarioDefs.Keys -join ', '), all)" }
}

if (-not $Out) { $Out = Join-Path $env:LOCALAPPDATA "VL.Qt\profiling\$(Get-Date -Format 'yyMMdd_HHmm')_$rid" }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

function Invoke-Native([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE) { throw "$what failed ($LASTEXITCODE)" }
}

# 1. Build + export ------------------------------------------------------------------------------------------------
if (-not $NoBuild) {
    Write-Host 'dotnet build src/VL.Qt -c Release'
    Invoke-Native 'dotnet build' { dotnet build "$repo\src\VL.Qt" -c Release -v q --nologo }
}
if (-not (Test-Path "$repo\runtimes\$rid\native\vlqt.dll")) {
    Write-Warning "runtimes\$rid\native\vlqt.dll missing: the exported program cannot run (tools/build-native.ps1 -Rid $rid)"
}

$patchDir = "$repo\tools\profiling"
& "$patchDir\make-assets.ps1"
$entries = foreach ($n in $names) {
    $def = $scenarioDefs[$n]
    $patch = $def.Patch
    $envVars = $def.Env
    if ($PatchOverride) {
        $vl = (Resolve-Path $PatchOverride).Path
        $patch = [IO.Path]::GetFileNameWithoutExtension($vl)
    }
    else {
        # Variant patches (Profile02_Elements500.vl) win over the shared patch + environment variable.
        if ($def.Variant -and (Test-Path "$patchDir\$($def.Variant).vl")) { $patch = $def.Variant; $envVars = @{} }
        $vl = "$patchDir\$patch.vl"
        if (-not (Test-Path $vl)) { throw "patch for scenario $n missing: $vl" }
    }
    [pscustomobject]@{ Name = $n; Patch = $patch; Vl = $vl; Env = $envVars }
}

$exes = @{}
foreach ($patch in ($entries | Select-Object -ExpandProperty Patch -Unique)) {
    $vl = ($entries | Where-Object Patch -eq $patch | Select-Object -First 1).Vl
    $dir = "$repo\build\profiling\$patch"
    Write-Host "export $patch -> $dir"
    # WinExe = window export (WinForms host): there the Qt renderer keeps the app alive, as in a user's export.
    # A console export (Exe) ends after the first frame without System.KeepAppAlive.
    & "$PSScriptRoot\vvvvc.ps1" -File $vl -Out $dir --rid $rid --output-type WinExe
    if ($LASTEXITCODE) { throw "vvvvc export of $patch failed ($LASTEXITCODE)" }
    # vvvvc leaves build intermediates (src\..\bin\Release, obj\apphost.exe) next to the real export <dir>\<Patch>\<Patch>.exe:
    # take the exe named like the patch, outside src\ and obj\, shortest path first.
    $found = @(Get-ChildItem $dir -Recurse -Filter '*.exe' | Where-Object { $_.FullName.Substring($dir.Length) -notmatch '\\(src|obj)\\' })
    $exe = $found | Where-Object { $_.BaseName -eq $patch } | Sort-Object { $_.FullName.Length } | Select-Object -First 1
    if (-not $exe) { $exe = $found | Sort-Object { $_.FullName.Length } | Select-Object -First 1 }
    if (-not $exe) { throw "no .exe found under $dir after export of $patch" }
    $exes[$patch] = $exe.FullName
}

# 2. Symbols -------------------------------------------------------------------------------------------------------
$symDirs = [System.Collections.Generic.List[string]]::new()
# The export carries vlqt.pdb next to vlqt.dll (runtimes\<rid>\native); fall back to the native build output.
$pdbs = @($exes.Values | ForEach-Object { Get-Item "$(Split-Path $_)\runtimes\$rid\native\vlqt.pdb" -ErrorAction SilentlyContinue })
if (-not $pdbs) { $pdbs = @(Get-ChildItem "$repo\build\$rid" -Recurse -Filter vlqt.pdb -ErrorAction SilentlyContinue | Select-Object -First 1) }
foreach ($pdb in $pdbs) { $symDirs.Add($pdb.DirectoryName) }
if (-not $pdbs) { Write-Warning "vlqt.pdb not found next to the exported vlqt.dll nor under build\$rid (tools/build-native.ps1 builds RelWithDebInfo); native frames of vlqt.dll stay unresolved" }
$qtVersion = '6.12.0'
$qtDir = "C:\Qt\$qtVersion\" + @{ arm64 = 'msvc2022_arm64'; x64 = 'msvc2022_64' }[$hostArch]
foreach ($d in "$qtDir\bin", "$qtDir\lib") {
    if (Test-Path "$d\Qt6Core.pdb") { $symDirs.Add($d) }
}
# Plugins (qjpeg, qwindows, ffmpegmediaplugin, ...) carry their PDBs in their own folders.
Get-ChildItem "$qtDir\plugins" -Recurse -Filter *.pdb -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty DirectoryName -Unique | ForEach-Object { $symDirs.Add($_) }
if (-not ($symDirs | Where-Object { Test-Path "$_\Qt6Core.pdb" })) { Write-Warning "no Qt PDBs under $qtDir; Qt frames stay unresolved. Install them with: tools/setup-qt.ps1 -DebugSymbols" }
$cache = Join-Path $env:LOCALAPPDATA 'VL.Qt\symbols'
New-Item -ItemType Directory -Force $cache | Out-Null
$symbolPath = (@($symDirs) + "SRV*$cache*https://msdl.microsoft.com/download/symbols") -join ';'

# 3. Plan ----------------------------------------------------------------------------------------------------------
$plan = [ordered]@{
    out        = $Out
    warmUp     = $WarmUp
    duration   = $Duration
    symbolPath = $symbolPath
    scenarios  = @($entries | ForEach-Object {
            [ordered]@{
                name   = $_.Name
                patch  = $_.Patch
                exe    = $exes[$_.Patch]
                env    = $_.Env
                output = Join-Path $Out "${rid}_$($_.Name)"
            }
        })
}
$planFile = Join-Path $Out 'plan.json'
$plan | ConvertTo-Json -Depth 6 | Set-Content $planFile -Encoding utf8NoBOM

# 4. Capture (one elevation) ---------------------------------------------------------------------------------------
$ultraPrefix = 'dnx --yes -- ultra'
if ($DryRun) {
    Write-Host "`n-DryRun: no capture. Plan: $planFile"
    foreach ($s in $plan.scenarios) {
        $envText = ($s.env.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value) " }) -join ''
        Write-Host "`n[$($s.name)] $envText"
        Write-Host "  $ultraPrefix profile --delay $WarmUp --duration $Duration --output `"$($s.output)`" --result-file `"$Out\$($s.name).result.json`" --symbol-path `"$symbolPath`" -- `"$($s.exe)`""
        Write-Host "  $ultraPrefix report <profile.json.gz> > `"$Out\$($s.name).report.txt`""
    }
    $captureExit = $null
}
else {
    Write-Host 'ultra warm-up (downloads the tool on first use)'
    & dnx --yes -- ultra --help *> $null
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $self = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-CaptureOnly', '-PlanFile', "`"$planFile`"")
    if ($isAdmin) {
        & pwsh @self
        $captureExit = $LASTEXITCODE
    }
    else {
        Write-Host "Recording $($plan.scenarios.Count) scenario(s) in an elevated shell (one UAC prompt); log: $Out\capture.log"
        $proc = Start-Process (Get-Process -Id $PID).Path -ArgumentList $self -Verb RunAs -Wait -PassThru
        $captureExit = $proc.ExitCode
    }
    Write-Host "capture exit code: $captureExit"
}

# 5. Reports -------------------------------------------------------------------------------------------------------
$manifestScenarios = foreach ($s in $plan.scenarios) {
    $result = Join-Path $Out "$($s.name).result.json"
    $profile = $null
    $report = $null
    if (-not $DryRun -and (Test-Path $result)) {
        $r = Get-Content -Raw $result | ConvertFrom-Json
        if ($r.success -and $r.profiles) {
            $profile = ($r.profiles | Sort-Object size -Descending | Select-Object -First 1).path
            $report = Join-Path $Out "$($s.name).report.txt"
            Write-Host "report $($s.name)"
            & dnx --yes -- ultra report --top 25 $profile 2>&1 | Set-Content $report -Encoding utf8NoBOM
        }
        else { Write-Warning "scenario $($s.name): capture failed ($($r.error))" }
    }
    [ordered]@{
        name    = $s.name
        patch   = $s.patch
        exe     = $s.exe
        env     = $s.env
        warmUp  = $WarmUp
        duration = $Duration
        result  = $result
        profile = $profile
        report  = $report
    }
}

# 6. Manifest ------------------------------------------------------------------------------------------------------
function Get-RefreshRate {
    $hz = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
        Where-Object CurrentRefreshRate | Select-Object -First 1 -ExpandProperty CurrentRefreshRate
    if ($hz) { return [int]$hz }
    # Some GPU drivers (e.g. Adreno) report nothing via WMI: ask the primary display (DEVMODEW, dmDisplayFrequency at 184).
    Add-Type -Namespace VlQt -Name Display -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern bool EnumDisplaySettingsW(string dev, int mode, byte[] devMode);' -ErrorAction SilentlyContinue
    $buf = New-Object byte[] 220
    [BitConverter]::GetBytes([uint16]220).CopyTo($buf, 68)
    if ([VlQt.Display]::EnumDisplaySettingsW($null, -1, $buf)) { return [int][BitConverter]::ToUInt32($buf, 184) }
    $null
}
$vvvvDir = if ($env:VVVV_DIR) { $env:VVVV_DIR } else {
    Get-ChildItem 'C:\vvvv' -Directory -Filter "vvvv_gamma_*-win-$hostArch" -ErrorAction SilentlyContinue |
        Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
}
$qtpaths = "$qtDir\bin\qtpaths6.exe"
if (Test-Path $qtpaths) { $qtVersion = (& $qtpaths --qt-version) }
$manifest = [ordered]@{
    created     = (Get-Date).ToString('o')
    mode        = if ($DryRun) { 'dryrun' } else { 'capture' }
    commit      = (git -C $repo rev-parse HEAD)
    dirty       = [bool](git -C $repo status --porcelain)
    rid         = $rid
    cpu         = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name).Trim()
    vvvv        = if ($vvvvDir) { Split-Path $vvvvDir -Leaf } else { $null }
    qt          = $qtVersion
    refreshRate = Get-RefreshRate
    captureExit = $captureExit
    symbolPath  = $symbolPath
    out         = $Out
    plan        = $planFile
    log         = Join-Path $Out 'capture.log'
    scenarios   = @($manifestScenarios)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Out 'manifest.json') -Encoding utf8NoBOM
Write-Host "manifest: $Out\manifest.json"
if ($captureExit) { exit 1 }
