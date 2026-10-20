@echo off
rem Restore GigaAM ONNX Runtime provider for NVIDIA RTX/CUDA machines.
chcp 65001 >nul
setlocal
set "HERE=%~dp0"
echo Restoring Audion Voice Tools for NVIDIA RTX/CUDA GPU...
rem The PowerShell the program carries comes first: App\ stays through a cleanup,
rem so the step runs on the same PowerShell on every machine.
set "PS_EXE="
if exist "%~dp0..\App\AudionPs.exe" set "PS_EXE=%~dp0..\App\AudionPs.exe"
if not defined PS_EXE where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"
if not defined PS_EXE set "PS_EXE=powershell.exe"
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%HERE%Install-GigaAM-ONNX.ps1" -Provider cuda
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" (
  echo [Audion Voice Tools] CUDA provider restored.
) else (
  echo [Audion Voice Tools] CUDA provider restore failed. Exit code: %RC%
)
call "%HERE%_pause_if_needed.cmd"
exit /b %RC%
