# Consumes a packed VL.Qt .nupkg (and VL.Qt.Stride, if present) the way a vvvv export with a NuGet dependency does (PackageReference from an exe project,
# RuntimeIdentifier set, dotnet publish) and checks the published layout (#315). No vvvv needed, so it runs in CI.
# Checks: publish succeeds (no NETSDK1152 for the qml tree), VL.Qt.dll next to the exe (lib/net10.0 recognized), the Qt
# runtime with its structure in runtimes/<rid>/native (QtNative, qml/QtQuick/Controls/qmldir), no qmldir flattened anywhere else.
# VL.Qt.Stride (Windows only): checked when VL.Qt.Stride.<version>.nupkg lies next to the VL.Qt one and contains VL.Qt.Stride.vl
# (a net10.0-windows consumer must get VL.Qt.Stride.dll plus VL.Qt through the dependency); otherwise that part is skipped with a message.
# Usage: tools/test-nupkg-consumer.ps1 -Package build/pack/VL.Qt.<version>.nupkg [-Rid win-x64] [-Work <dir>]
#   (a VL.Qt.Stride.<version>.nupkg as -Package is replaced by its VL.Qt sibling)
# Uses its own NuGet package folder under -Work, so a reused test version never comes from the global cache.
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Package,
    [string]$Rid = 'win-x64',
    [string]$Work = "$PSScriptRoot\..\build\nupkg-consumer"
)
$ErrorActionPreference = 'Stop'

$Package = (Resolve-Path $Package).Path
$feed = Split-Path $Package
if ((Split-Path $Package -Leaf) -match '^VL\.Qt\.Stride\.(\d.+)\.nupkg$') { $Package = "$feed\VL.Qt.$($Matches[1]).nupkg" }
if ((Split-Path $Package -Leaf) -notmatch '^VL\.Qt\.(\d.+)\.nupkg$' -or -not (Test-Path $Package)) { throw "not a VL.Qt .nupkg: $Package" }
$version = $Matches[1]
$stridePackage = "$feed\VL.Qt.Stride.$version.nupkg"
$Work = [IO.Path]::GetFullPath($Work, (Get-Location).Path)
if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
$proj = New-Item -ItemType Directory -Force "$Work\app"
$out = "$Work\out"

@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config><add key="globalPackagesFolder" value="$Work\packages" /></config>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="VL.Qt" /><package pattern="VL.Qt.Stride" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content "$proj\nuget.config"

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <RuntimeIdentifier>$Rid</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="VL.Qt" Version="$version" />
  </ItemGroup>
</Project>
"@ | Set-Content "$proj\Consumer.csproj"
'System.Console.WriteLine("VL.Qt consumer");' | Set-Content "$proj\Program.cs"

# Directory.Build.* of the repo must not leak into the consumer project.
$env:ImportDirectoryBuildProps = 'false'; $env:ImportDirectoryBuildTargets = 'false'; $env:ImportDirectoryPackagesProps = 'false'
& dotnet publish "$proj\Consumer.csproj" -c Release -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$qtNative = if ($Rid -like 'win-*') { 'vlqt.dll' } else { 'libvlqt.so' }
$native = "runtimes/$Rid/native"
$need = 'VL.Qt.dll', "$native/$qtNative", "$native/qml/QtQuick/Controls/qmldir", "$native/plugins"
$missing = @($need | Where-Object { -not (Test-Path (Join-Path $out $_)) })
if ($missing) { throw "missing in publish output: $($missing -join ', ')" }
$qmldirs = @(Get-ChildItem $out -Recurse -Filter qmldir | ForEach-Object { [IO.Path]::GetRelativePath($out, $_.FullName) -replace '\\', '/' })
$stray = @($qmldirs | Where-Object { $_ -notlike "$native/qml/*" })
if ($stray) { throw "qmldir outside $native/qml: $($stray -join ', ')" }
$otherRids = @(Get-ChildItem "$out/runtimes" -Directory | Where-Object Name -ne $Rid | ForEach-Object Name)
if ($otherRids) { throw "runtimes of other RIDs in the output: $($otherRids -join ', ')" }
"VL.Qt $version consumed for ${Rid}: {0} files under $native, {1} qmldir, layout ok" -f
    @(Get-ChildItem "$out/$native" -Recurse -File).Count, $qmldirs.Count

# VL.Qt.Stride: a windows-TFM consumer; its only dependency (VL.Qt, same version) must come along.
if ($Rid -notlike 'win-*') { return "VL.Qt.Stride: skipped, Windows only ($Rid)" }
if (-not (Test-Path $stridePackage)) { return "VL.Qt.Stride: skipped, no $(Split-Path $stridePackage -Leaf) next to the VL.Qt package" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($stridePackage)
try { $hasVl = [bool]($zip.Entries | Where-Object FullName -eq 'VL.Qt.Stride.vl') } finally { $zip.Dispose() }
if (-not $hasVl) { return "VL.Qt.Stride: skipped, $(Split-Path $stridePackage -Leaf) has no VL.Qt.Stride.vl yet (created in vvvv, task 2.5)" }

$strideProj = New-Item -ItemType Directory -Force "$Work\app-stride"
Copy-Item "$proj\nuget.config", "$proj\Program.cs" $strideProj
(Get-Content "$proj\Consumer.csproj" -Raw) -replace 'net10\.0<', 'net10.0-windows<' -replace '<PackageReference Include="VL.Qt" Version="[^"]+" />',
    "<PackageReference Include=`"VL.Qt.Stride`" Version=`"$version`" />" | Set-Content "$strideProj\Consumer.csproj"
$strideOut = "$Work\out-stride"
& dotnet publish "$strideProj\Consumer.csproj" -c Release -o $strideOut -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (VL.Qt.Stride) failed ($LASTEXITCODE)" }
$needStride = 'VL.Qt.Stride.dll', 'VL.Qt.dll', "$native/$qtNative"
$missingStride = @($needStride | Where-Object { -not (Test-Path (Join-Path $strideOut $_)) })
if ($missingStride) { throw "missing in VL.Qt.Stride publish output: $($missingStride -join ', ')" }
"VL.Qt.Stride $version consumed for ${Rid}: VL.Qt.Stride.dll, VL.Qt.dll and QtNative present"
