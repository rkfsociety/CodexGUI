@echo off
setlocal
cd /d "%~dp0"
dotnet build "%~dp0CodexGUI.csproj" --configuration Release --nologo --verbosity quiet
if errorlevel 1 (
  pause
  exit /b 1
)
start "" "%~dp0bin\Release\net10.0-windows\CodexGUI.exe"
endlocal
