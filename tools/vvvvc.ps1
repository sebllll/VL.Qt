# Compiles/exports a .vl document with vvvvc (vvvv's CLI compiler). Exit code 0 = no build errors.
# Usage: tools/vvvvc.ps1 [-File help/HelloWorld.vl] [-VvvvDir <install>] [-Out build/vvvvc] [extra vvvvc args]
# VvvvDir defaults to $env:VVVV_DIR, else the newest C:\vvvv\vvvv_gamma_*-win-<host arch>.
# Unnamed arguments all go to vvvvc (e.g. `tools/vvvvc.ps1 -File x.vl --rid win-x64 --output-type Exe`).
# Note: a .props next to the patch overrides --rid/--output-type (it is imported after the generated settings).
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$File,
    [string]$VvvvDir = $env:VVVV_DIR,
    [string]$Out,
    [Parameter(ValueFromRemainingArguments)] [string[]]$VvvvcArgs
)
$ErrorActionPreference = 'Stop'

# Defaults here, not in param(): Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults of advanced scripts.
if (-not $File) { $File = "$PSScriptRoot\..\help\HelloWorld.vl" }
if (-not $Out) { $Out = "$PSScriptRoot\..\build\vvvvc" }

# Machine scope: an emulated x64 parent (e.g. Git Bash on arm64) passes AMD64 down in the process env.
$hostArch = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'arm64' } else { 'x64' }
if (-not $VvvvDir) {
    $VvvvDir = Get-ChildItem 'C:\vvvv' -Directory -Filter "vvvv_gamma_*-win-$hostArch" |
        Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $VvvvDir) { throw "no vvvv_gamma_*-win-$hostArch under C:\vvvv; pass -VvvvDir" }
}
$dll = "$VvvvDir\vvvvc.dll"
if (-not (Test-Path $dll)) { throw "vvvvc.dll not found in $VvvvDir" }

function Get-Machine([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    [BitConverter]::ToUInt16($b, [BitConverter]::ToInt32($b, 0x3c) + 4)
}

# The 8.0 arm64 previews ship vvvvc.dll (pure IL) flagged as x64, so it won't load on arm64.
# Workaround: a copy with the PE machine field set to arm64, plus deps/runtimeconfig pointing at it.
if ($hostArch -eq 'arm64' -and (Get-Machine $dll) -eq 0x8664) {
    $shim = "$VvvvDir\vvvvc.arm64.dll"
    if (-not (Test-Path $shim) -or (Get-Item $shim).LastWriteTime -lt (Get-Item $dll).LastWriteTime) {
        $b = [IO.File]::ReadAllBytes($dll)
        $pe = [BitConverter]::ToInt32($b, 0x3c)
        $b[$pe + 4] = 0x64; $b[$pe + 5] = 0xAA
        [IO.File]::WriteAllBytes($shim, $b)
        (Get-Content -Raw "$VvvvDir\vvvvc.deps.json") -replace '"vvvvc\.dll"', '"vvvvc.arm64.dll"' |
            Set-Content -NoNewline "$VvvvDir\vvvvc.arm64.deps.json"
        Copy-Item "$VvvvDir\vvvvc.runtimeconfig.json" "$VvvvDir\vvvvc.arm64.runtimeconfig.json"
        Write-Host "Created arm64 shim $shim"
    }
    $dll = $shim
}

# vvvvc needs an absolute document path (relative dependencies fail otherwise).
$File = (Resolve-Path $File).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

Write-Host "vvvvc ($(Split-Path $VvvvDir -Leaf)): $File -> $Out"
& dotnet $dll $File --output-directory $Out -v Warning @VvvvcArgs 2>&1 |
    Where-Object { $_ -notmatch 'package source|nuget-package-source-mapping|^warn: packman' }
exit $LASTEXITCODE
