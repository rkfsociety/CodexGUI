@echo off
setlocal
set "projectDir=%~dp0.."
dotnet build "%projectDir%\Build\CodexGUI.csproj" --configuration Release --nologo --verbosity quiet
if errorlevel 1 (
  pause
  exit /b 1
)
start "" "%projectDir%\Build\bin\Release\net10.0-windows\CodexGUI.exe"
endlocal
