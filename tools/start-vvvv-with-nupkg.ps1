# Startet vvvv mit einem VL.Qt-nupkg (ohne Installation): das neueste VL.Qt.<version>.nupkg aus -NupkgDir,
# plus VL.Qt.Stride.<gleiche version>.nupkg, falls dort vorhanden,
# Default = Ordner dieses Scripts (Script also neben das nupkg kopieren oder -NupkgDir angeben).
#   .\start-vvvv-with-nupkg.ps1                          # nur vvvv + Paket
#   .\start-vvvv-with-nupkg.ps1 -Help "Draw a Rectangle" # HowTo-Patch aus dem Paket öffnen
#   .\start-vvvv-with-nupkg.ps1 -Patch C:\foo\bar.vl     # beliebigen Patch öffnen
# vvvv: -VvvvDir, sonst $env:VVVV_DIR, sonst neueste C:\vvvv\vvvv_gamma_*-win-<arch>.
param([string]$Help, [string]$Patch, [string]$NupkgDir = $PSScriptRoot, [string]$VvvvDir = $env:VVVV_DIR)
$ErrorActionPreference = 'Stop'

# '^VL\.Qt\.\d' keeps VL.Qt.Stride.*.nupkg out of the VL.Qt match.
$nupkg = Get-ChildItem $NupkgDir -File -Filter 'VL.Qt.*.nupkg' | Where-Object Name -match '^VL\.Qt\.\d' |
    Sort-Object { [version]($_.BaseName -replace '^VL\.Qt\.' -replace '-.*$') } |
    Select-Object -Last 1
if (-not $nupkg) { throw "kein VL.Qt.*.nupkg in $NupkgDir" }
$ver = $nupkg.BaseName -replace '^VL\.Qt\.'
# VL.Qt.Stride (Windows, same version) is optional.
$strideNupkg = Get-Item (Join-Path $NupkgDir "VL.Qt.Stride.$ver.nupkg") -ErrorAction SilentlyContinue

# Pro Version/Dateistand einmal entpacken; vvvv will <repo>\<PackageId>\...
$repo = Join-Path $env:LOCALAPPDATA "VL.Qt-nupkg\$ver"
function Expand-Package([IO.FileInfo]$file, [string]$id) {
    $pkg = Join-Path $repo $id
    $stamp = Join-Path $repo ".source-$id"
    $src = "$($file.FullName)|$($file.Length)|$($file.LastWriteTimeUtc.Ticks)"
    if (-not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $src) {
        if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
        Expand-Archive $file.FullName $pkg
        Set-Content $stamp $src
        Write-Host "entpackt: $($file.Name) -> $pkg"
    }
    $pkg
}
$pkg = Expand-Package $nupkg 'VL.Qt'
$pkgs = @($pkg)
if ($strideNupkg) { $pkgs += Expand-Package $strideNupkg 'VL.Qt.Stride' }

if (-not $VvvvDir) {
    $arch = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'arm64' } else { 'x64' }
    $VvvvDir = Get-ChildItem 'C:\vvvv' -Directory -Filter "vvvv_gamma_*-win-$arch" |
        Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $VvvvDir) { throw "keine vvvv_gamma_*-win-$arch unter C:\vvvv; -VvvvDir angeben" }
}

if ($Help) {
    $Patch = $pkgs | ForEach-Object { Join-Path $_ 'help' } | Where-Object { Test-Path $_ } | Get-ChildItem -Recurse -Filter '*.vl' |
        Where-Object BaseName -like "*$Help*" | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Patch) { throw "kein Help-Patch passend zu '$Help' in $repo\*\help" }
}

# Patch-Pfad MUSS vor --package-repositories stehen, sonst wird er als weiteres Repo gelesen.
$vvvvArgs = @()
if ($Patch) { $vvvvArgs += "`"$Patch`"" }
$vvvvArgs += "--package-repositories `"$repo`""
Write-Host "vvvv:  $VvvvDir"
Write-Host "Paket: VL.Qt $ver$(if ($strideNupkg) { ' + VL.Qt.Stride' })"
if ($Patch) { Write-Host "Patch: $Patch" }
Start-Process (Join-Path $VvvvDir 'vvvv.exe') -ArgumentList ($vvvvArgs -join ' ')
