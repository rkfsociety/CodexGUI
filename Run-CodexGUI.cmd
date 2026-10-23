@echo off
setlocal
dotnet build "%~dp0Build\CodexGUI.csproj" --configuration Release --nologo --verbosity quiet
if errorlevel 1 (
  pause
  exit /b 1
)
start "" "%~dp0Build\bin\Release\net10.0-windows\CodexGUI.exe"
endlocal
