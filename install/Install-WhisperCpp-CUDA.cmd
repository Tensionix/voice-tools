@echo off
rem Opt-in: whisper.cpp on CUDA/cuBLAS for an NVIDIA card, in place of the CPU pack in Tools\whispercpp.
rem Its model is large-v2 (Install-WhisperCpp-Large-Models.cmd): turbo loops on CUDA, so it is not downloaded, and
rem one left from the CPU pack goes to the Recycle Bin. Install-Live-Vulkan.cmd puts the CPU pack and turbo back.
chcp 65001 >nul
setlocal
rem The PowerShell the program carries comes first: App\ stays through a cleanup,
rem so the step runs on the same PowerShell on every machine.
set "PS_EXE="
if exist "%~dp0..\App\AudionPs.exe" set "PS_EXE=%~dp0..\App\AudionPs.exe"
if not defined PS_EXE where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"
if not defined PS_EXE set "PS_EXE=powershell.exe"
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Live-Vulkan.ps1" -Pack cublas %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo.
  echo [Audion Voice Tools] whisper.cpp CUDA pack install did not complete. Exit code: %RC%
  goto DONE
)
echo.
echo [Audion Voice Tools] whisper.cpp CUDA pack installed.

:DONE
call "%~dp0_pause_if_needed.cmd"
exit /b %RC%
