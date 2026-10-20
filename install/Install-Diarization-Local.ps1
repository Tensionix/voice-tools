[CmdletBinding()]
param(
    [switch]$Force
)

# Speaker separation without a HuggingFace key: Sortformer (diar_streaming_sortformer_4spk-v2.1, NVIDIA Open Model
# License, the GGUF of handy-computer) on the engine of Handy - transcribe.cpp 0.4.0, the same Tools\transcribe the
# dictation loads - through its Python binding transcribe_cpp 0.4.0 (MIT). The binding and the model are pinned by
# their SHA-256; the binding is installed without its native wheel: the engine of dictation is its native part.
# sherpa-onnx, the engine without a key before it, is taken off: its package uninstalled, its models to the Recycle Bin.

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$InstallDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $PSCommandPath }
$Root = (Resolve-Path (Join-Path $InstallDir "..")).Path
$Python = Join-Path $Root "runtime\python.exe"
$Engine = Join-Path $Root "Tools\transcribe\transcribe.dll"
$Models = Join-Path $Root "models\transcribe"
$Download = Join-Path $InstallDir "download\sortformer"
$Marker = Join-Path $Root "Tools\diarization\audion-sortformer-pack.txt"

$Version = "0.4.0"
$WheelName = "transcribe_cpp-$Version-py3-none-any.whl"
$WheelUrl = "https://github.com/handy-computer/transcribe.cpp/releases/download/v$Version/$WheelName"
$WheelSha = "c6dd091848006c7881d46b4066aa687782f49302c4253c0cb45396976cb0abcd"

$ModelRepo = "handy-computer/diar_streaming_sortformer_4spk-v2.1-gguf"
$ModelRevision = "60ebab3ef7cae9869be0abcde13360347e96ba03"
$ModelName = "diar_streaming_sortformer_4spk-v2.1-Q8_0.gguf"
$ModelSha = "a5dacdc650790266c7a362e54e6bf51952015487edaa606c4e11632bc32442a9"
$ModelBase = "https://huggingface.co/$ModelRepo/resolve/$ModelRevision"
$Model = Join-Path $Models $ModelName

$StepTotal = 5

function Write-Step {
    param([int]$Step, [string]$Label)
    Write-Host ("[audion-step] {0}/{1} {2}" -f $Step, $StepTotal, $Label)
}

function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ("{0:N2} GB" -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ("{0:N1} MB" -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ("{0:N1} KB" -f ($Bytes / 1KB)) }
    return ("{0:N0} B" -f $Bytes)
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Download-FileWithProgress {
    param([string]$Url, [string]$Destination, [string]$Label)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    $temp = "$Destination.part"
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    Add-Type -AssemblyName System.Net.Http
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromHours(2)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Url)
    $request.Headers.UserAgent.ParseAdd("Audion-Voice-Tools")
    Write-Host "$Label download started: $Url"
    $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $null = $response.EnsureSuccessStatusCode()
    $total = $response.Content.Headers.ContentLength
    $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $output = [System.IO.FileStream]::new($temp, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    try {
        $buffer = New-Object byte[] (1024 * 1024)
        $done = 0L
        $lastDone = 0L
        $lastTick = Get-Date
        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $output.Write($buffer, 0, $read)
            $done += $read
            $now = Get-Date
            $elapsed = ($now - $lastTick).TotalSeconds
            if ($elapsed -ge 2.0) {
                $speed = [Math]::Max(0, ($done - $lastDone) / [Math]::Max($elapsed, 0.1))
                $totalText = if ($total) { Format-Size $total } else { "unknown" }
                $pctText = if ($total) { "{0:N1}%" -f (($done / $total) * 100.0) } else { "?" }
                Write-Host ("{0}: {1} / {2} ({3}) @ {4}/s" -f $Label, (Format-Size $done), $totalText, $pctText, (Format-Size $speed))
                $lastDone = $done
                $lastTick = $now
            }
        }
    } finally {
        $output.Dispose()
        $source.Dispose()
        $response.Dispose()
        $client.Dispose()
        $handler.Dispose()
    }
    Move-Item -LiteralPath $temp -Destination $Destination -Force
    Write-Host "$Label download complete: $(Format-Size (Get-Item -LiteralPath $Destination).Length)"
}

# A file is taken only with its pinned SHA-256: a wrong one is deleted and fetched once more.
function Get-PinnedFile {
    param([string]$Url, [string]$Destination, [string]$Sha, [string]$Label)
    if ((Test-Path -LiteralPath $Destination) -and (Get-Sha256 $Destination) -eq $Sha) {
        Write-Host "$Label already in place and verified."
        return
    }
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
        Download-FileWithProgress -Url $Url -Destination $Destination -Label $Label
        $got = Get-Sha256 $Destination
        if ($got -eq $Sha) {
            Write-Host "$Label SHA-256 verified."
            return
        }
        Write-Warning "$Label SHA-256 differs (got $got, expected $Sha); attempt $attempt of 2."
        Remove-Item -LiteralPath $Destination -Force
    }
    throw "$Label could not be downloaded with the expected SHA-256."
}

if (-not (Test-Path -LiteralPath $Python)) {
    throw "Portable Python was not found: $Python. Run Build_Portable_Env.cmd first."
}

Write-Host "Audion Voice Tools - speakers without a key: Sortformer on transcribe.cpp $Version"
Write-Host ""

Write-Step 1 "The engine of transcribe.cpp"
if (Test-Path -LiteralPath $Engine) {
    Write-Host "The engine is in place: $Engine"
} else {
    # The engine of dictation alone - the model of GigaAM is not needed for the speakers.
    & (Join-Path $InstallDir "Install-Dictation-Engine.ps1") -EngineOnly
    if (-not (Test-Path -LiteralPath $Engine)) { throw "The engine of transcribe.cpp did not come into place: $Engine" }
}

Write-Step 2 "The binding transcribe_cpp $Version"
$wheel = Join-Path $Download $WheelName
Get-PinnedFile -Url $WheelUrl -Destination $wheel -Sha $WheelSha -Label $WheelName
# Without its native wheel: the native part is the engine of dictation, Tools\transcribe.
& $Python -m pip install --disable-pip-version-check --no-warn-script-location --no-deps --no-index --force-reinstall $wheel
if ($LASTEXITCODE -ne 0) { throw "pip did not install $WheelName (exit code $LASTEXITCODE)." }
# The binding takes and gives arrays of numpy. It used to come with GigaAM for files; the build brings no model now, so the
# speakers may be the first to need it: the wheel cache of the build first, PyPI when it is not there.
& $Python -c "import numpy" *> $null
if ($LASTEXITCODE -ne 0) {
    $pip = @("-m", "pip", "install", "--disable-pip-version-check", "--no-warn-script-location", "numpy==2.3.5")
    $cache = Join-Path $Root "wheelhouse\cpu"
    if (Test-Path -LiteralPath $cache) { $pip += @("--find-links", $cache) }
    & $Python @pip
    if ($LASTEXITCODE -ne 0) { throw "pip did not install numpy (exit code $LASTEXITCODE)." }
}

Write-Step 3 "Sortformer 4spk v2.1 Q8_0 (GGUF)"
if (-not $Force -and (Test-Path -LiteralPath $Model) -and (Get-Item -LiteralPath $Model).Length -eq 139310336) {
    Write-Host "The model is in place: $Model"
} else {
    Get-PinnedFile -Url "$ModelBase/$ModelName" -Destination $Model -Sha $ModelSha -Label $ModelName
}
foreach ($name in @("LICENSE", "NOTICE")) {
    $beside = Join-Path $Models "$($ModelRepo.Split('/')[1]).$name.txt"
    if ($Force -or -not (Test-Path -LiteralPath $beside)) {
        Download-FileWithProgress -Url "$ModelBase/$name" -Destination $beside -Label "model $name"
    }
}

Write-Step 4 "sherpa-onnx taken off"
& $Python -m pip show sherpa-onnx *> $null
if ($LASTEXITCODE -eq 0) {
    & $Python -m pip uninstall --disable-pip-version-check -y sherpa-onnx
}
$oldModels = Join-Path $Root "models\sherpa-diarization"
if (Test-Path -LiteralPath $oldModels) {
    try {
        # The Recycle Bin of the shell: 0x14 - no question, no progress window.
        (New-Object -ComObject Shell.Application).Namespace(10).MoveHere($oldModels, 0x14)
        Start-Sleep -Milliseconds 500
        Write-Host "The models of sherpa-onnx went to the Recycle Bin: $oldModels"
    } catch {
        Write-Warning "The models of sherpa-onnx stay in $oldModels - they are no longer used: $($_.Exception.Message)"
    }
}
$oldMarker = Join-Path $Root "Tools\diarization\audion-sherpa-diarization-pack.txt"
if (Test-Path -LiteralPath $oldMarker) { Remove-Item -LiteralPath $oldMarker -Force }

Write-Step 5 "Verify"
$check = @"
import os, sys
os.environ['TRANSCRIBE_LIBRARY'] = sys.argv[1]
import numpy as np, transcribe_cpp as tc
for backend in ('vulkan', 'cpu'):
    try:
        model = tc.Model(sys.argv[2], backend=backend)
        break
    except Exception as error:
        last = error
else:
    raise SystemExit(f'Sortformer did not load: {last}')
model.diarize_session().run(np.zeros(32000, dtype=np.float32))
print(f'Sortformer runs on {backend}: {model.device.description}')
"@
& $Python -X utf8 -c $check $Engine $Model
if ($LASTEXITCODE -ne 0) { throw "Sortformer does not run (exit code $LASTEXITCODE)." }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Marker) | Out-Null
Set-Content -LiteralPath $Marker -Encoding UTF8 -Value @(
    "binding=transcribe_cpp $Version",
    "model=$ModelName",
    "revision=$ModelRevision",
    "installed_at=$((Get-Date).ToString('s'))"
)
Write-Host "[OK] Speakers without a key are ready: Sortformer on transcribe.cpp $Version."
