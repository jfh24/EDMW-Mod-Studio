[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'EDMW Mod Studio must be built on Windows.'
}
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 9 SDK or a newer compatible SDK before building EDMW Mod Studio.'
}

$stagingRoot = Join-Path $PSScriptRoot 'publish-staging'

$native = Join-Path $PSScriptRoot 'native\EDMWAnchored\build-native.cmd'
& $native
if ($LASTEXITCODE -ne 0) { throw "Native generator build failed with exit code $LASTEXITCODE" }

$installerProject = Join-Path $PSScriptRoot 'EDMWMultiplayerInstaller\EDMWMultiplayerInstaller.csproj'
$installerAsset = Join-Path $PSScriptRoot 'EDMWMapDesigner\Assets'
$installerOutput = Join-Path $stagingRoot 'map-installer'
dotnet publish $installerProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false -o $installerOutput
if ($LASTEXITCODE -ne 0) { throw "Multiplayer installer build failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $installerOutput 'EDMWMultiplayerInstaller.exe') `
    -Destination (Join-Path $installerAsset 'EDMWMultiplayerInstaller.exe') -Force

$civilizationInstallerProject = Join-Path $PSScriptRoot 'EDMWCivilizationInstaller\EDMWCivilizationInstaller.csproj'
$civilizationInstallerOutput = Join-Path $stagingRoot 'civilization-installer'
dotnet publish $civilizationInstallerProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false -o $civilizationInstallerOutput
if ($LASTEXITCODE -ne 0) { throw "Civilization installer build failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $civilizationInstallerOutput 'EDMWCivilizationInstaller.exe') `
    -Destination (Join-Path $installerAsset 'EDMWCivilizationInstaller.exe') -Force

$project = Join-Path $PSScriptRoot 'EDMWMapDesigner\EDMWMapDesigner.csproj'
$output = Join-Path $PSScriptRoot 'dist'
$stagingOutput = Join-Path $stagingRoot 'mod-studio'
$resolvedRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$resolvedOutput = [System.IO.Path]::GetFullPath($output).TrimEnd('\')

if ($resolvedOutput -ne (Join-Path $resolvedRoot 'dist')) {
    throw "Refusing to clean unexpected output path: $resolvedOutput"
}
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false -o $stagingOutput
if ($LASTEXITCODE -ne 0) { throw "EDMW Mod Studio publish failed with exit code $LASTEXITCODE" }
$publishedExecutable = Join-Path $stagingOutput 'EDMW Mod Studio.exe'
if (!(Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "The published executable is missing: $publishedExecutable"
}
# Keep the tracked distribution README when refreshing the generated executable.
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
Copy-Item -LiteralPath $publishedExecutable -Destination (Join-Path $resolvedOutput 'EDMW Mod Studio.exe') -Force

Write-Host "Built EDMW Mod Studio: $publishedExecutable"
Write-Host "Distribution: $resolvedOutput\EDMW Mod Studio.exe"
