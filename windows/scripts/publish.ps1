# Builds the Windows release asset: a self-contained single-file PokeTokenBar.exe zipped as
# PokeTokenBar-Windows-x64.zip (the name UpdateChecker looks for: contains "windows", ends in .zip).
# Usage: pwsh windows/scripts/publish.ps1 [-Version 2.5.7]
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts'
$publish = Join-Path $out 'publish'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

dotnet test (Join-Path $root 'tests/PokeTokenBar.Core.Tests') -c Release
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
dotnet test (Join-Path $root 'tests/PokeTokenBar.App.Tests') -c Release
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

$props = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
           '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $publish)
if ($Version) { $props += "-p:Version=$Version" }
dotnet publish (Join-Path $root 'src/PokeTokenBar.App') @props
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$zip = Join-Path $out 'PokeTokenBar-Windows-x64.zip'
Compress-Archive -Path (Join-Path $publish 'PokeTokenBar.exe') -DestinationPath $zip
Write-Host "Built $zip"
