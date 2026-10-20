@echo off
rem Opt-in: GPU diarization stack (torch CUDA + pyannote.audio 4 + Community-1 weights). Heavy (~3.7 GB).
rem Requires a CUDA GPU and a HuggingFace key: config\api_key_huggingface.txt, or the key row on the
rem SETUP of the window, KEYS OF THE SERVICES. Accept the terms of pyannote/speaker-diarization-community-1 with that account first.
chcp 65001 >nul
setlocal
set "ROOT=%~dp0.."
set "PYEXE=%ROOT%\runtime\python.exe"
if not exist "%PYEXE%" set "PYEXE=python"

rem The GUI passes the key as HF_TOKEN; the builder menu reads the key file itself.
if not defined HF_TOKEN if exist "%ROOT%\config\api_key_huggingface.txt" set /p HF_TOKEN=<"%ROOT%\config\api_key_huggingface.txt"
set "HF_HOME=%ROOT%\models\huggingface"
set "HF_HUB_CACHE=%ROOT%\models\huggingface\hub"
set "HF_HUB_DISABLE_SYMLINKS_WARNING=1"

echo [audion-step] 1/3 Install torch (CUDA)
echo This is a large download and only makes sense on a machine with a CUDA GPU.
"%PYEXE%" -m pip install --progress-bar raw --no-warn-script-location --index-url https://download.pytorch.org/whl/cu128 "torch==2.8.0+cu128" "torchaudio==2.8.0+cu128"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto DONE

echo [audion-step] 2/3 Install pyannote.audio
"%PYEXE%" -m pip install --progress-bar raw --no-warn-script-location --constraint "%ROOT%\install\constraints_cuda.txt" "pyannote.audio>=4.0,<5.0"
set "RC=%ERRORLEVEL%"
rem The PowerShell the program carries comes first: App\ stays through a cleanup,
rem so the step runs on the same PowerShell on every machine.
set "PS_EXE="
if exist "%~dp0..\App\AudionPs.exe" set "PS_EXE=%~dp0..\App\AudionPs.exe"
if not defined PS_EXE where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"
if not defined PS_EXE set "PS_EXE=powershell.exe"
if "%RC%"=="0" if exist "%ROOT%\Tools\gigaam\audion-gigaam-onnx-pack.txt" (
  echo Restoring GigaAM ONNX provider after CUDA/pyannote install...
  "%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\install\Restore-GigaAM-ONNX-Provider.ps1"
  call set "RC=%%ERRORLEVEL%%"
)
if not "%RC%"=="0" goto DONE

echo [audion-step] 3/3 Download Community-1 weights
if not defined HF_TOKEN (
  echo [WARN] No HuggingFace key: the weights were not downloaded.
  echo        Enter the key in SETUP of the window, KEYS OF THE SERVICES, and run this installer again.
  set "RC=3"
  goto DONE
)
"%PYEXE%" -W ignore -c "import os; from pyannote.audio import Pipeline; p = Pipeline.from_pretrained('pyannote/speaker-diarization-community-1', token=os.environ['HF_TOKEN']); print('Community-1 weights: OK' if p is not None else 'Community-1 weights: access refused'); raise SystemExit(0 if p is not None else 4)"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo [ERROR] HuggingFace refused the model. Open https://huggingface.co/pyannote/speaker-diarization-community-1
  echo         with the same account, accept the terms, then run this installer again.
  goto DONE
)

echo.
echo Done. Enable diarization to use it; CUDA transcription itself uses resident whisper.cpp/cuBLAS.

:DONE
call "%~dp0_pause_if_needed.cmd"
endlocal & exit /b %RC%
