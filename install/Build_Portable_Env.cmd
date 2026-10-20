@echo off
rem Build the portable Python environment via PowerShell.
chcp 65001 >nul
setlocal
set "HERE=%~dp0"

rem The PowerShell the program carries comes first: App\ stays through a cleanup,
rem so the step runs on the same PowerShell on every machine.
set "PS_EXE="
if exist "%~dp0..\App\AudionPs.exe" set "PS_EXE=%~dp0..\App\AudionPs.exe"
if not defined PS_EXE where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"
if not defined PS_EXE set "PS_EXE=powershell.exe"
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%HERE%Build_Portable_Env.ps1" %*
set "RC=%ERRORLEVEL%"

:DONE
call "%~dp0_pause_if_needed.cmd"
endlocal & exit /b %RC%
