# Consumes a packed VL.Qt .nupkg the way a vvvv export with a NuGet dependency does (PackageReference from an exe project,
# RuntimeIdentifier set, dotnet publish) and checks the published layout (#315). No vvvv needed, so it runs in CI.
# Checks: publish succeeds (no NETSDK1152 for the qml tree), VL.Qt.dll next to the exe (lib/net10.0 recognized), the Qt
# runtime with its structure in runtimes/<rid>/native (QtNative, qml/QtQuick/Controls/qmldir), no qmldir flattened anywhere else.
# Usage: tools/test-nupkg-consumer.ps1 -Package build/pack/VL.Qt.<version>.nupkg [-Rid win-x64] [-Work <dir>]
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
if ((Split-Path $Package -Leaf) -notmatch '^VL\.Qt\.(.+)\.nupkg$') { throw "not a VL.Qt .nupkg: $Package" }
$version = $Matches[1]
$feed = Split-Path $Package
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
    <packageSource key="local"><package pattern="VL.Qt" /></packageSource>
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
