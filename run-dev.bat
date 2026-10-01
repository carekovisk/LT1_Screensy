@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK is not installed. Run build.bat once first.
  exit /b 1
)

dotnet run --project "%CD%\ScreensyWindowAudio\ScreensyWindowAudio.csproj"
