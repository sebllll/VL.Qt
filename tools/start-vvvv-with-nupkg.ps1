# Startet vvvv mit einem VL.Qt-nupkg (ohne Installation): das neueste VL.Qt.*.nupkg aus -NupkgDir,
# Default = Ordner dieses Scripts (Script also neben das nupkg kopieren oder -NupkgDir angeben).
#   .\start-vvvv-with-nupkg.ps1                          # nur vvvv + Paket
#   .\start-vvvv-with-nupkg.ps1 -Help "Draw a Rectangle" # HowTo-Patch aus dem Paket öffnen
#   .\start-vvvv-with-nupkg.ps1 -Patch C:\foo\bar.vl     # beliebigen Patch öffnen
# vvvv: -VvvvDir, sonst $env:VVVV_DIR, sonst neueste C:\vvvv\vvvv_gamma_*-win-<arch>.
param([string]$Help, [string]$Patch, [string]$NupkgDir = $PSScriptRoot, [string]$VvvvDir = $env:VVVV_DIR)
$ErrorActionPreference = 'Stop'

$nupkg = Get-ChildItem $NupkgDir -File -Filter 'VL.Qt.*.nupkg' |
    Sort-Object { [version]($_.BaseName -replace '^VL\.Qt\.' -replace '-.*$') } |
    Select-Object -Last 1
if (-not $nupkg) { throw "kein VL.Qt.*.nupkg in $NupkgDir" }
$ver = $nupkg.BaseName -replace '^VL\.Qt\.'

# Pro Version/Dateistand einmal entpacken; vvvv will <repo>\<PackageId>\...
$repo = Join-Path $env:LOCALAPPDATA "VL.Qt-nupkg\$ver"
$pkg  = Join-Path $repo 'VL.Qt'
$stamp = Join-Path $repo '.source'
$src = "$($nupkg.FullName)|$($nupkg.Length)|$($nupkg.LastWriteTimeUtc.Ticks)"
if (-not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $src) {
    if (Test-Path $repo) { Remove-Item $repo -Recurse -Force }
    Expand-Archive $nupkg.FullName $pkg
    Set-Content $stamp $src
    Write-Host "entpackt: $($nupkg.Name) -> $pkg"
}

if (-not $VvvvDir) {
    $arch = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'arm64' } else { 'x64' }
    $VvvvDir = Get-ChildItem 'C:\vvvv' -Directory -Filter "vvvv_gamma_*-win-$arch" |
        Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $VvvvDir) { throw "keine vvvv_gamma_*-win-$arch unter C:\vvvv; -VvvvDir angeben" }
}

if ($Help) {
    $Patch = Get-ChildItem (Join-Path $pkg 'help') -Recurse -Filter '*.vl' |
        Where-Object BaseName -like "*$Help*" | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Patch) { throw "kein Help-Patch passend zu '$Help' in $pkg\help" }
}

# Patch-Pfad MUSS vor --package-repositories stehen, sonst wird er als weiteres Repo gelesen.
$vvvvArgs = @()
if ($Patch) { $vvvvArgs += "`"$Patch`"" }
$vvvvArgs += "--package-repositories `"$repo`""
Write-Host "vvvv:  $VvvvDir"
Write-Host "Paket: VL.Qt $ver"
if ($Patch) { Write-Host "Patch: $Patch" }
Start-Process (Join-Path $VvvvDir 'vvvv.exe') -ArgumentList ($vvvvArgs -join ' ')
