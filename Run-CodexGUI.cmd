@echo off
setlocal
set "APP_EXE=%~dp0Build\bin\Portable\CodexGUI.exe"
dotnet publish "%~dp0Build\CodexGUI.csproj" -p:PublishProfile=Portable --nologo --verbosity quiet
if errorlevel 1 goto publish_failed
if not exist "%APP_EXE%" goto publish_failed
start "" "%APP_EXE%"
endlocal
exit /b 0

:publish_failed
echo Failed to publish the self-contained CodexGUI executable.
pause
endlocal
exit /b 1
