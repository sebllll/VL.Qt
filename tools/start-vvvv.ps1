# Startet vvvv mit VL.Qt aus diesem Repo als Source-Paket (Dev-Workflow, kein nupkg).
#   tools\start-vvvv.ps1                              # vvvv + VL.Qt aus dem Repo
#   tools\start-vvvv.ps1 -Build                       # vorher QtNative + C# bauen
#   tools\start-vvvv.ps1 -Help "Draw a Rectangle"     # HowTo-Patch aus help\ öffnen
#   tools\start-vvvv.ps1 -Patch help\testpatch.vl     # beliebigen Patch öffnen
#   tools\start-vvvv.ps1 -PrepareOnly                 # nur das Package-Repo (Junction) anlegen, für F5 in VS
# Repo: -RepoDir, sonst der Ordner über diesem Script.
# vvvv: -VvvvDir, sonst $env:VVVV_DIR, sonst neueste C:\vvvv\vvvv_gamma_*-win-<arch>.
param(
    [string]$Help,
    [string]$Patch,
    [switch]$Build,
    [switch]$PrepareOnly,
    [string]$RepoDir = (Split-Path $PSScriptRoot -Parent),
    [string]$VvvvDir = $env:VVVV_DIR
)
$ErrorActionPreference = 'Stop'

$RepoDir = (Resolve-Path $RepoDir).Path
if (-not (Test-Path (Join-Path $RepoDir 'VL.Qt.vl'))) { throw "kein VL.Qt.vl in $RepoDir" }
$arch = if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -eq 'ARM64') { 'arm64' } else { 'x64' }

if ($Build) {
    & (Join-Path $RepoDir 'tools\build-native.ps1') -Rid "win-$arch"
    dotnet build (Join-Path $RepoDir 'src\VL.Qt') -c Release -v q -nologo
    if ($LASTEXITCODE) { throw "dotnet build fehlgeschlagen ($LASTEXITCODE)" }
}

# vvvv erwartet <repo>\<PackageId>\<PackageId>.vl. Der Repo-Ordner heißt evtl. anders (Worktree, VL.QT)
# und sein Elternordner enthält andere Pakete (C:\vl\VL.StandardLibs) -> eigenes Repo mit Junction.
$key = ($RepoDir -replace '[:\\/ ]', '_').Trim('_')
$pkgRepo = Join-Path $env:LOCALAPPDATA "VL.Qt-dev\$key"
$link = Join-Path $pkgRepo 'VL.Qt'
if ((Test-Path $link) -and (Get-Item $link).Target -ne $RepoDir) { (Get-Item $link).Delete() }
if (-not (Test-Path $link)) {
    New-Item -ItemType Directory -Force $pkgRepo | Out-Null
    New-Item -ItemType Junction -Path $link -Target $RepoDir | Out-Null
}
if ($PrepareOnly) { Write-Host "Package-Repo: $pkgRepo"; return }

if (-not $VvvvDir) {
    $VvvvDir = Get-ChildItem 'C:\vvvv' -Directory -Filter "vvvv_gamma_*-win-$arch" |
        Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $VvvvDir) { throw "keine vvvv_gamma_*-win-$arch unter C:\vvvv; -VvvvDir angeben" }
}

if ($Help) {
    $Patch = Get-ChildItem (Join-Path $RepoDir 'help') -Recurse -Filter '*.vl' |
        Where-Object BaseName -like "*$Help*" | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Patch) { throw "kein Help-Patch passend zu '$Help' in $RepoDir\help" }
}
elseif ($Patch) {
    $inRepo = Join-Path $RepoDir $Patch
    $Patch = if (Test-Path $inRepo) { (Resolve-Path $inRepo).Path } else { (Resolve-Path $Patch).Path }
}

# Patch-Pfad MUSS vor --package-repositories stehen, sonst wird er als weiteres Repo gelesen.
$vvvvArgs = @()
if ($Patch) { $vvvvArgs += "`"$Patch`"" }
$vvvvArgs += "--package-repositories `"$pkgRepo`""
Write-Host "vvvv:  $VvvvDir"
Write-Host "Repo:  $RepoDir"
if ($Patch) { Write-Host "Patch: $Patch" }
Start-Process (Join-Path $VvvvDir 'vvvv.exe') -ArgumentList ($vvvvArgs -join ' ')
