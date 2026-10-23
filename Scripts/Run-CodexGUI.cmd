@echo off
setlocal
set "projectDir=%~dp0.."
dotnet build "%projectDir%\CodexGUI.csproj" --configuration Release --nologo --verbosity quiet
if errorlevel 1 (
  pause
  exit /b 1
)
start "" "%projectDir%\bin\Release\net10.0-windows\CodexGUI.exe"
endlocal
