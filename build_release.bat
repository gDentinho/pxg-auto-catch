@echo off
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

echo ============================================================
echo PxG Auto Catch v0.10.3 - COMPATIBILITY RESOLVER / BRIDGE V9 RACE-GUARD
echo ============================================================
echo.

if exist "%~dp0InputSender.cs" (
    echo Removendo arquivo obsoleto: InputSender.cs
    del /F /Q "%~dp0InputSender.cs" >nul 2>&1
)

set "DOTNET_EXE="

if exist "%~dp0.dotnet\dotnet.exe" set "DOTNET_EXE=%~dp0.dotnet\dotnet.exe"
if not defined DOTNET_EXE if exist "C:\Program Files\dotnet\dotnet.exe" set "DOTNET_EXE=C:\Program Files\dotnet\dotnet.exe"

if defined DOTNET_EXE (
    "%DOTNET_EXE%" --list-sdks | findstr /b "8." >nul 2>&1
    if errorlevel 1 set "DOTNET_EXE="
)

if not defined DOTNET_EXE (
    echo [ERRO] .NET 8 SDK nao encontrado.
    pause
    exit /b 1
)

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "%VSWHERE%" (
    echo [ERRO] vswhere.exe nao encontrado.
    echo Instale Visual Studio Build Tools 2022 com Desktop development with C++.
    pause
    exit /b 1
)

for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath`) do set "VSROOT=%%i"

if not defined VSROOT (
    echo [ERRO] Visual Studio Build Tools nao encontrado.
    pause
    exit /b 1
)

set "MSBUILD=!VSROOT!\MSBuild\Current\Bin\MSBuild.exe"

if not exist "!MSBUILD!" (
    echo [ERRO] MSBuild nao encontrado em:
    echo !MSBUILD!
    pause
    exit /b 1
)

for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%i"
if not defined STAMP set "STAMP=build"

set "OUT=publish_!STAMP!"
set "LAUNCHER_OUT=launcher_publish_!STAMP!"
set "RELEASE_ZIP=PxGAutoCatch-v0.10.3-win-x64.zip"

echo.
echo Limpando caches de build anteriores...
if exist "%~dp0obj" rmdir /S /Q "%~dp0obj"
if exist "%~dp0bin" rmdir /S /Q "%~dp0bin"
if exist "%~dp0Launcher\obj" rmdir /S /Q "%~dp0Launcher\obj"
if exist "%~dp0Launcher\bin" rmdir /S /Q "%~dp0Launcher\bin"

echo.
echo [1/3] Compilando Internal Bridge v9 race-guard x64...
echo MSBuild: !MSBUILD!
echo.

"!MSBUILD!" "%~dp0Bridge\PxGCorpseBridge.vcxproj" /p:Configuration=Release /p:Platform=x64 /m

if errorlevel 1 (
    echo.
    echo [ERRO] BUILD DA BRIDGE FALHOU.
    pause
    exit /b 1
)

if not exist "%~dp0native_bin\PxGCorpseBridge_v9.dll" (
    echo.
    echo [ERRO] PxGCorpseBridge_v9.dll nao foi gerada.
    pause
    exit /b 1
)

echo.
echo [2/3] Publicando aplicativo principal .NET...
echo.

"!DOTNET_EXE!" publish "%~dp0PxGCorpseReader.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "%~dp0!OUT!"

if errorlevel 1 (
    echo.
    echo [ERRO] BUILD .NET PRINCIPAL FALHOU.
    pause
    exit /b 1
)

echo.
echo [3/3] Publicando Launcher / Updater...
echo.

"!DOTNET_EXE!" publish "%~dp0Launcher\PxGAutoCatchLauncher.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "%~dp0!LAUNCHER_OUT!"

if errorlevel 1 (
    echo.
    echo [ERRO] BUILD DO LAUNCHER FALHOU.
    pause
    exit /b 1
)

copy /Y "%~dp0native_bin\PxGCorpseBridge_v9.dll" "%~dp0!OUT!\PxGCorpseBridge_v9.dll" >nul
copy /Y "%~dp0!LAUNCHER_OUT!\PxGAutoCatch.exe" "%~dp0!OUT!\PxGAutoCatch.exe" >nul
copy /Y "%~dp0update_config.json" "%~dp0!OUT!\update_config.json" >nul

if not exist "%~dp0!OUT!\PxGCorpseReader.exe" (
    echo [ERRO] PxGCorpseReader.exe nao foi gerado.
    pause
    exit /b 1
)

if not exist "%~dp0!OUT!\PxGAutoCatch.exe" (
    echo [ERRO] PxGAutoCatch.exe nao foi gerado.
    pause
    exit /b 1
)

if not exist "%~dp0!OUT!\PxGCorpseBridge_v9.dll" (
    echo [ERRO] PxGCorpseBridge_v9.dll nao foi copiada para o publish.
    pause
    exit /b 1
)

if exist "%~dp0!RELEASE_ZIP!" del /F /Q "%~dp0!RELEASE_ZIP!" >nul 2>&1
powershell -NoProfile -Command "Compress-Archive -Path '%~dp0!OUT!\*' -DestinationPath '%~dp0!RELEASE_ZIP!' -Force"

for /f %%i in ('powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 '%~dp0!RELEASE_ZIP!').Hash"') do set "RELEASE_SHA=%%i"
> "%~dp0!RELEASE_ZIP!.sha256" echo !RELEASE_SHA!  !RELEASE_ZIP!

echo.
echo ============================================================
echo BUILD OK
echo.
echo Abra normalmente por:
echo   %~dp0!OUT!\PxGAutoCatch.exe
echo.
echo Core:
echo   %~dp0!OUT!\PxGCorpseReader.exe
echo Bridge:
echo   %~dp0!OUT!\PxGCorpseBridge_v9.dll
echo.
echo Pacote para GitHub Release:
echo   %~dp0!RELEASE_ZIP!
echo SHA-256:
echo   !RELEASE_SHA!
echo Arquivo SHA:
echo   %~dp0!RELEASE_ZIP!.sha256
echo.
echo O Launcher encontra esses dois assets automaticamente na GitHub Release.
echo ============================================================
echo.

rmdir /S /Q "%~dp0!LAUNCHER_OUT!" >nul 2>&1
pause
