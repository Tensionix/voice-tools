@echo off
setlocal EnableExtensions
chcp 65001 >nul

rem Audion Voice Tools: the console command. The window runs files through it.
rem
rem   voice_cli.cmd describe
rem   voice_cli.cmd run D:\Meetings --set language=ru --result outcome.json
rem   voice_cli.cmd run talk.m4a --set diarization.enabled=true --cancel-file stop.flag
rem   voice_cli.cmd version
rem
rem Exit codes: 0 done, 1 a file failed, 2 wrong call or nothing to process, 130 stopped.

set "BASE_DIR=%~dp0"
if "%BASE_DIR:~-1%"=="\" set "BASE_DIR=%BASE_DIR:~0,-1%"

set "PYTHON_CMD="
if exist "%BASE_DIR%\runtime\python.exe" set "PYTHON_CMD=%BASE_DIR%\runtime\python.exe"
rem Not a block in brackets: a path with a bracket in it - "Program Files (x86)" - would close it.
if not defined PYTHON_CMD goto NO_PYTHON

rem The command is started as a file, not as a module: it puts the project on the path itself, wherever the embedded
rem Python looks for modules.
set "PYTHONUTF8=1"
set "PYTHONIOENCODING=utf-8"
"%PYTHON_CMD%" -u "%BASE_DIR%\system_core\cli.py" %*
exit /b %ERRORLEVEL%

:NO_PYTHON
echo [ERR] Python of the project was not found: "%BASE_DIR%\runtime\python.exe"
echo       Build the environment first: builder_main.cmd
exit /b 2
