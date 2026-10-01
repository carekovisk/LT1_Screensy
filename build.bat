@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================================
echo  Lightone Stream - Release Build
echo ============================================================
echo.

set "RID=win-x64"
if /I "%~1"=="arm64" set "RID=win-arm64"
if /I "%~1"=="x64" set "RID=win-x64"

set "DOTNET_CMD=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
    set "DOTNET_CMD=%CD%\.dotnet\dotnet.exe"
    if not exist "%CD%\.dotnet\dotnet.exe" (
        echo .NET 8 SDK was not found. Installing a private copy in .dotnet...
        echo This does not modify the system-wide .NET installation.
        echo.

        powershell -NoProfile -ExecutionPolicy Bypass -Command ^
          "$ErrorActionPreference='Stop'; Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%CD%\dotnet-install.ps1'"
        if errorlevel 1 goto :error

        powershell -NoProfile -ExecutionPolicy Bypass -File "%CD%\dotnet-install.ps1" -Channel 8.0 -InstallDir "%CD%\.dotnet" -NoPath
        if errorlevel 1 goto :error
    )
)

if exist "%CD%\dist\%RID%" rmdir /s /q "%CD%\dist\%RID%"
mkdir "%CD%\dist\%RID%" >nul 2>nul

echo Building %RID% single-file portable EXE...
"%DOTNET_CMD%" publish "%CD%\ScreensyWindowAudio\ScreensyWindowAudio.csproj" ^
    -c Release ^
    -r %RID% ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:PublishTrimmed=false ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o "%CD%\dist\%RID%"
if errorlevel 1 goto :error

echo.
echo ============================================================
echo  BUILD COMPLETE
echo ============================================================
echo Output:
echo   %CD%\dist\%RID%\Lightone-Stream.exe
echo.
echo The EXE is self-contained for .NET.
echo Microsoft Edge WebView2 Runtime 141+ is still required.
echo.
exit /b 0

:error
echo.
echo BUILD FAILED.
echo See the messages above for details.
exit /b 1
