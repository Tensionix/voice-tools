@echo off
rem The engine of dictation: transcribe.cpp 0.4.0 (CPU + Vulkan) into Tools\transcribe and
rem GigaAM v3 RNN-T Q8_0 (GGUF) into models\transcribe, both pinned by their SHA-256.
setlocal
chcp 65001 >nul

set "HERE=%~dp0"
rem The PowerShell the program carries comes first: App\ stays through a cleanup,
rem so the step runs on the same PowerShell on every machine.
set "PS_EXE="
if exist "%~dp0..\App\AudionPs.exe" set "PS_EXE=%~dp0..\App\AudionPs.exe"
if not defined PS_EXE where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"
if not defined PS_EXE set "PS_EXE=powershell.exe"
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%HERE%Install-Dictation-Engine.ps1" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
  echo.
  echo [Audion Voice Tools] The engine of dictation was not installed. Exit code: %RC%
  goto DONE
)

echo.
echo [Audion Voice Tools] The engine of dictation is installed.

:DONE
call "%~dp0_pause_if_needed.cmd"
exit /b %RC%
