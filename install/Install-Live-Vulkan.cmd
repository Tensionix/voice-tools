@echo off
setlocal
chcp 65001 >nul

set "HERE=%~dp0"
rem The PowerShell the program carries comes first: App\ stays through a cleanup,
rem so the step runs on the same PowerShell on every machine.
set "PS_EXE="
if exist "%~dp0..\App\AudionPs.exe" set "PS_EXE=%~dp0..\App\AudionPs.exe"
if not defined PS_EXE where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"
if not defined PS_EXE set "PS_EXE=powershell.exe"
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%HERE%Install-Live-Vulkan.ps1" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
  echo.
  echo [Audion Voice Tools] whisper.cpp local pack install did not complete. Exit code: %RC%
  goto DONE
)

echo.
echo [Audion Voice Tools] whisper.cpp local pack installed.

:DONE
call "%~dp0_pause_if_needed.cmd"
exit /b %RC%
