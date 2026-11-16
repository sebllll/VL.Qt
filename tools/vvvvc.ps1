# Compiles/exports a .vl document with vvvvc (vvvv's CLI compiler). Exit code 0 = no build errors.
# Usage: tools/vvvvc.ps1 [-File help/HelloWorld.vl] [-VvvvDir <install>] [-Out build/vvvvc] [extra vvvvc args]
# VvvvDir defaults to $env:VVVV_DIR, else the newest C:\vvvv\vvvv_gamma_*-win-<host arch>.
# Unnamed arguments all go to vvvvc (e.g. `tools/vvvvc.ps1 -File x.vl --rid win-x64 --output-type Exe`).
# A patch in VL.Qt.Stride/ or VL.Qt.Audio/ (or depending on one of them) automatically gets the dev package repository of
# start-vvvv.ps1, editable VL.Qt packages and a local feed with them packed (needs tools/build.ps1 first; see the comment below).
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
# A patch in VL.Qt.Stride/ or VL.Qt.Audio/ or one that depends on one of them needs that package, which no feed has before the release.
# vvvv loads VL.Qt and the companion package from source (the dev package repository of start-vvvv.ps1, junctions to this repo),
# but an export also writes a PackageReference without a version for the package of each assembly. So:
#  - --package-repositories gives vvvvc the sources of the .vl documents,
#  - --editable-packages makes the exporter compile them as projects,
#  - VL.Qt and each needed companion package are packed from the build output of this repo into build/vvvvc-feed (version
#    $devVersion, the lowest version in every feed, so NuGet picks it for the unversioned reference) and added as a package source.
# Needs the C# build (tools/build.ps1) first. Patches of VL.Qt alone resolve VL.Qt through their relative DocumentDependency.
# An explicit --package-repositories wins.
$repoDir = (Resolve-Path "$PSScriptRoot\..").Path
$companions = @(
    @{ Name = 'VL.Qt.Stride'; Dll = 'VL.Qt.Stride\lib\net10.0-windows7.0\VL.Qt.Stride.dll' },
    @{ Name = 'VL.Qt.Audio'; Dll = 'VL.Qt.Audio\lib\net10.0-windows7.0\VL.Qt.Audio.dll' }
) | Where-Object {
    $File.StartsWith("$repoDir\$($_.Name)\", [StringComparison]::OrdinalIgnoreCase) -or
    ($File -like '*.vl' -and (Select-String -Path $File -SimpleMatch $_.Name -Quiet))
}
if ($companions -and -not ($VvvvcArgs -contains '--package-repositories')) {
    $devVersion = '0.0.1'
    $feed = "$repoDir\build\vvvvc-feed"
    $packages = @('VL.Qt') + @($companions | ForEach-Object { $_.Name })
    $inputs = @("$repoDir\lib\net10.0\VL.Qt.dll", "$repoDir\VL.Qt.vl") +
        @($companions | ForEach-Object { "$repoDir\$($_.Dll)"; "$repoDir\$($_.Name)\$($_.Name).vl" })
    foreach ($i in $inputs) { if (-not (Test-Path $i)) { throw "$i not found: run tools/build.ps1 first" } }
    $newest = ($inputs + (Get-ChildItem "$repoDir\runtimes" -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object FullName) |
        ForEach-Object { (Get-Item $_).LastWriteTimeUtc } | Measure-Object -Maximum).Maximum
    $stale = $packages | Where-Object { -not (Test-Path "$feed\$_.$devVersion.nupkg") -or (Get-Item "$feed\$_.$devVersion.nupkg").LastWriteTimeUtc -lt $newest }
    if ($stale) {
        Write-Host "Packing $($packages -join ', ') $devVersion into $feed"
        New-Item -ItemType Directory -Force $feed | Out-Null
        foreach ($name in $packages) {
            # the same version may sit in the global NuGet cache from an earlier pack
            Remove-Item "$env:USERPROFILE\.nuget\packages\$($name.ToLowerInvariant())\$devVersion" -Recurse -Force -ErrorAction SilentlyContinue
        }
        foreach ($name in $packages) {
            & dotnet pack "$repoDir\src\$name" -c Release --no-build -v q -nologo -p:Version=$devVersion -o $feed | Out-Null
            if ($LASTEXITCODE) { throw "dotnet pack $name failed ($LASTEXITCODE)" }
        }
        # The source package (junction VL.Qt) already copies runtimes/<rid>/native next to the exe (VL.Qt.vl.targets); the
        # qt/ tree of the package would copy it a second time (NETSDK1152, same files twice in the publish output).
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::Open("$feed\VL.Qt.$devVersion.nupkg", 'Update')
        try { @($zip.Entries | Where-Object { $_.FullName.StartsWith('qt/') }) | ForEach-Object { $_.Delete() } }
        finally { $zip.Dispose() }
    }
    $pkgRepo = (& "$PSScriptRoot\start-vvvv.ps1" -PrepareOnly -RepoDir $repoDir) | Select-Object -Last 1
    $VvvvcArgs = @($VvvvcArgs) + '--package-repositories', $pkgRepo, '--editable-packages', ($packages -join ';'), '--export-package-sources', $feed
}

New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

Write-Host "vvvvc ($(Split-Path $VvvvDir -Leaf)): $File -> $Out"
& dotnet $dll $File --output-directory $Out -v Warning @VvvvcArgs 2>&1 |
    Where-Object { $_ -notmatch 'package source|nuget-package-source-mapping|^warn: packman' }
exit $LASTEXITCODE
