@echo off
cd /d "%~dp0"
if exist "dist\0.3.0\MicBridge.exe" (
    start "" "dist\0.3.0\MicBridge.exe"
) else if exist "artifacts\0.3.0\win-x64\MicBridge.exe" (
    start "" "artifacts\0.3.0\win-x64\MicBridge.exe"
) else (
    dotnet run --project src\MicBridge.App -f net48
    if errorlevel 1 pause
)
