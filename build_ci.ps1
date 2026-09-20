param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dist = Join-Path $Root "dist"
$NativeBin = Join-Path $Root "native_bin"
$AppOut = Join-Path $Dist "app"
$LauncherOut = Join-Path $Dist "launcher"

# Avoid nested SDK-generated AssemblyInfo files being globbed by the root project.
foreach ($dir in @(
    (Join-Path $Root "obj"),
    (Join-Path $Root "bin"),
    (Join-Path $Root "Launcher\obj"),
    (Join-Path $Root "Launcher\bin")
)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
New-Item -ItemType Directory -Path $AppOut | Out-Null
New-Item -ItemType Directory -Path $LauncherOut | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe não encontrado." }

$vsroot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsroot) { throw "Visual Studio/MSBuild não encontrado." }

$msbuild = Join-Path $vsroot "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild não encontrado em $msbuild" }

& $msbuild (Join-Path $Root "Bridge\PxGCorpseBridge.vcxproj") /p:Configuration=$Configuration /p:Platform=x64 /m
if ($LASTEXITCODE -ne 0) { throw "Build da bridge falhou." }

$bridge = Join-Path $NativeBin "PxGCorpseBridge_v9.dll"
if (-not (Test-Path $bridge)) { throw "PxGCorpseBridge_v9.dll não foi gerada." }

& dotnet publish (Join-Path $Root "PxGCorpseReader.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $AppOut
if ($LASTEXITCODE -ne 0) { throw "Publish do app falhou." }

& dotnet publish (Join-Path $Root "Launcher\PxGAutoCatchLauncher.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $LauncherOut
if ($LASTEXITCODE -ne 0) { throw "Publish do launcher falhou." }

Copy-Item $bridge (Join-Path $AppOut "PxGCorpseBridge_v9.dll") -Force
Copy-Item (Join-Path $LauncherOut "PxGAutoCatch.exe") (Join-Path $AppOut "PxGAutoCatch.exe") -Force
Copy-Item (Join-Path $Root "update_config.json") (Join-Path $AppOut "update_config.json") -Force

$versionXml = [xml](Get-Content (Join-Path $Root "PxGCorpseReader.csproj"))
$version = $versionXml.Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw "Version não encontrada no csproj." }

$zipName = "PxGAutoCatch-v$version-win-x64.zip"
$zipPath = Join-Path $Dist $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $AppOut "*") -DestinationPath $zipPath -Force

$hash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash
$shaPath = "$zipPath.sha256"
Set-Content -Path $shaPath -Value "$hash  $zipName" -Encoding ascii

Write-Host "PACKAGE=$zipPath"
Write-Host "SHA256=$hash"
Write-Host "SHA_FILE=$shaPath"
