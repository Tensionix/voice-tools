@echo off
rem Verify the portable environment: Python, base deps, ffmpeg, OpenAI key.
chcp 65001 >nul
setlocal
set "ROOT=%~dp0.."
set "PYEXE=%ROOT%\runtime\python.exe"
if not exist "%PYEXE%" (
  echo [ERROR] Portable Python runtime was not found:
  echo   %PYEXE%
  echo Run Build_Portable_Env.cmd first.
  call "%~dp0_pause_if_needed.cmd"
  endlocal & exit /b 1
)

rem The PowerShell the program carries comes first: App\ stays through a cleanup,
rem so the step runs on the same PowerShell on every machine.
set "PS_EXE="
if exist "%~dp0..\App\AudionPs.exe" set "PS_EXE=%~dp0..\App\AudionPs.exe"
if not defined PS_EXE where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"
if not defined PS_EXE set "PS_EXE=powershell.exe"
echo === Python ===
"%PYEXE%" --version
echo === Base dependencies ===
"%PYEXE%" -c "import openai, pydantic, slugify, yaml; print('base deps OK')"
echo === Live deps ===
"%PYEXE%" -c "import importlib.util as u; print('sounddevice:', 'OK' if u.find_spec('sounddevice') else 'missing'); print('websockets:', 'OK' if u.find_spec('websockets') else 'missing')"
echo === Local STT deps ===
"%PYEXE%" -c "import importlib.util as u; print('onnx_asr:', 'OK' if u.find_spec('onnx_asr') else 'missing'); print('onnxruntime:', 'OK' if u.find_spec('onnxruntime') else 'missing'); print('torch:', 'OK' if u.find_spec('torch') else 'missing'); print('pyannote:', 'OK' if u.find_spec('pyannote') else 'missing'); legacy=[name for name in ('faster_whisper','ctranslate2') if u.find_spec(name)]; print('legacy Faster-Whisper/CTranslate2:', ('FOUND: ' + ', '.join(legacy)) if legacy else 'absent (expected)')"
echo === GigaAM ONNX pack ===
if exist "%ROOT%\Tools\gigaam\audion-gigaam-onnx-pack.txt" (echo GigaAM marker: OK) else (echo GigaAM marker: missing - the window offers it, SETUP)
"%PYEXE%" -c "import importlib.util as u; ok=u.find_spec('onnxruntime') is not None; exec('import onnxruntime as ort; blocked={\"TensorrtExecutionProvider\",\"NvTensorRTRTXExecutionProvider\",\"NvTensorRtRtxExecutionProvider\"}; providers=[p for p in ort.get_available_providers() if p not in blocked]; print(\"ONNX providers:\", \", \".join(providers))' if ok else 'print(\"ONNX providers: missing\")')"
echo === FFmpeg ===
if exist "%ROOT%\Tools\ffmpeg\bin\ffprobe.exe" (echo ffprobe: portable OK) else (echo ffprobe: NOT installed - run Install-Portable-FFmpeg-BtbN.cmd or Install-Portable-FFmpeg-Gyan.cmd)
echo === whisper.cpp local pack ===
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$tools=Join-Path '%ROOT%' 'Tools\whispercpp'; $names=@('whisper-server.exe','server.exe','whisper-cli.exe','main.exe','whisper.exe'); if (Test-Path $tools) { $bin=Get-ChildItem -LiteralPath $tools -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.Name.ToLowerInvariant() } | Select-Object -First 1; if ($bin) { Write-Host ('whisper.cpp: binary present - ' + $bin.FullName) } else { Write-Host 'whisper.cpp: NOT installed - the window offers it, SETUP' } } else { Write-Host 'whisper.cpp: NOT installed - the window offers it, SETUP' }"
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$tools=Join-Path '%ROOT%' 'Tools\whispercpp'; $marker=Join-Path $tools 'audion-whispercpp-pack.txt'; $kind=''; if (Test-Path $marker) { $line=Get-Content $marker | Where-Object { $_ -match '^kind=' } | Select-Object -First 1; if ($line) { $kind=($line -replace '^kind=','').Trim().ToLowerInvariant(); if ($kind -eq 'vulkan') { $kind='manual' } } }; if (-not $kind -and (Test-Path $tools)) { $files=Get-ChildItem -LiteralPath $tools -Recurse -File -ErrorAction SilentlyContinue; if ($files | Where-Object { $_.Name -match '(?i)(cublas|cuda)' } | Select-Object -First 1) { $kind='cublas' } elseif ($files | Where-Object { $_.Name -match '(?i)vulkan' } | Select-Object -First 1) { $kind='manual' } elseif ($files) { $kind='cpu' } }; if ($kind -eq 'cublas') { Write-Host 'whisper.cpp backend: CUDA/cuBLAS GPU pack' } elseif ($kind -eq 'manual') { Write-Host 'whisper.cpp backend: custom GPU pack (manual; not auto-selected)' } elseif ($kind -eq 'cpu') { Write-Host 'whisper.cpp backend: CPU fallback pack' } else { Write-Host 'whisper.cpp backend: unknown/missing' }"
echo === whisper.cpp models ===
rem The model goes with the pack: large-v2 on the CUDA pack (turbo loops there), turbo on the CPU pack.
set "WHISPER_PACK=cpu"
if exist "%ROOT%\Tools\whispercpp\audion-whispercpp-pack.txt" findstr /i /c:"kind=cublas" /c:"kind=cuda" "%ROOT%\Tools\whispercpp\audion-whispercpp-pack.txt" >nul && set "WHISPER_PACK=cublas"
if /I "%WHISPER_PACK%"=="cublas" (
  if exist "%ROOT%\models\ggml-large-v2.bin" (echo Large V2 model: OK - files on CUDA) else (echo Large V2 model: missing - the window offers it, SETUP)
  if exist "%ROOT%\models\ggml-large-v3-turbo.bin" (echo Turbo model: not used on CUDA - Install-WhisperCpp-CUDA.cmd moves it to the Recycle Bin) else (echo Turbo model: not used on CUDA)
) else (
  if exist "%ROOT%\models\ggml-large-v3-turbo.bin" (echo Turbo model: OK) else (echo Turbo model: missing - the window offers it, SETUP)
  if exist "%ROOT%\models\ggml-large-v2.bin" (echo Large V2 model: OK) else (echo Large V2 model: optional missing)
)
if exist "%ROOT%\models\ggml-small.bin" (echo Small model: OK) else (echo Small model: optional missing)
echo === Health check ===
"%PYEXE%" "%ROOT%\system_core\main.py"
set "RC=%ERRORLEVEL%"
call "%~dp0_pause_if_needed.cmd"
endlocal & exit /b %RC%
