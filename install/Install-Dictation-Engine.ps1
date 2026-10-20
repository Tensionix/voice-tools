[CmdletBinding()]
param(
    [switch]$Force,
    # The engine alone, without the model of GigaAM: for the speakers of Sortformer.
    [switch]$EngineOnly,
    # Another model of dictation from config\dictation_models.json (parakeet-v3, whisper-turbo...): the engine, then that model.
    [string]$ModelId = ""
)

# The engine of dictation: transcribe.cpp (Handy's engine, MIT) built for the CPU and Vulkan into Tools\transcribe,
# and GigaAM v3 e2e RNN-T Q8_0 in GGUF (MIT) into models\transcribe. The window calls the engine directly
# (Source\Core\Engine.cs); its C API is the one of 0.4.0, so the version and both files are pinned by their SHA-256.
# A second run downloads nothing that is already in place; -Force puts both in place again. -ModelId puts another model of
# config\dictation_models.json in place of GigaAM - pinned the same way, its licence beside it; DICTATIONS chooses among them.

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$InstallDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $PSCommandPath }
$Root = (Resolve-Path (Join-Path $InstallDir "..")).Path
$Tools = Join-Path $Root "Tools\transcribe"
$Models = Join-Path $Root "models\transcribe"
$Download = Join-Path $InstallDir "download\transcribe"
$Marker = Join-Path $Tools "audion-transcribe-pack.txt"

$EngineVersion = "0.4.0"
$PackName = "transcribe-native-$EngineVersion-windows-x86_64-cpu-vulkan.tar.gz"
$PackUrl = "https://github.com/handy-computer/transcribe.cpp/releases/download/v$EngineVersion/$PackName"
$PackSha = "9d5bd89f720d7979323e73f5220fe50fad5e38a7b0c2525225d453bd09adf2b0"
$PackFolder = "transcribe-native-windows-x86_64-cpu-vulkan"

$ModelRepo = "handy-computer/gigaam-v3-e2e-rnnt-gguf"
$ModelRevision = "3efd5b39882ceadca5570a8fb9fa1a2c801d6d6d"
$ModelName = "gigaam-v3-e2e-rnnt-Q8_0.gguf"
$ModelSha = "78d63b47723b7f8d78c6113a6ef983b5a86e2a86f6c273e1f5cb6967b1c4467a"
$ModelBytes = 273724832
$ModelTitle = "GigaAM v3 RNN-T Q8_0"
if ($ModelId) {
    $catalogPath = Join-Path $Root "config\dictation_models.json"
    $entry = (Get-Content -Raw -LiteralPath $catalogPath -Encoding UTF8 | ConvertFrom-Json).models | Where-Object { $_.id -eq $ModelId } | Select-Object -First 1
    if (-not $entry) { throw "No model '$ModelId' in config\dictation_models.json." }
    $ModelRepo = $entry.repo
    $ModelRevision = $entry.revision
    $ModelName = $entry.file
    $ModelSha = $entry.sha256
    $ModelBytes = [int64]$entry.bytes
    $ModelTitle = $entry.title
}
$ModelBase = "https://huggingface.co/$ModelRepo/resolve/$ModelRevision"
$ModelPath = Join-Path $Models $ModelName

$StepTotal = 4

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
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null

    $temp = "$Destination.part"
    $existing = 0L
    if (Test-Path -LiteralPath $temp) {
        $existing = [int64](Get-Item -LiteralPath $temp).Length
    }

    Add-Type -AssemblyName System.Net.Http
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromHours(6)

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Url)
    $request.Headers.UserAgent.ParseAdd("Audion-Voice-Tools")
    if ($existing -gt 0) {
        $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new($existing, $null)
        Write-Host "$Label resume: $(Format-Size $existing)"
    } else {
        Write-Host "$Label download started: $Url"
    }

    $response = $client.SendAsync(
        $request,
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
    ).GetAwaiter().GetResult()

    if ($existing -gt 0 -and [int]$response.StatusCode -eq 200) {
        Write-Host "$Label server ignored resume; restarting clean download."
        $existing = 0L
        if (Test-Path -LiteralPath $temp) {
            Remove-Item -LiteralPath $temp -Force
        }
    }

    $null = $response.EnsureSuccessStatusCode()
    $remaining = $response.Content.Headers.ContentLength
    $total = $null
    $range = $response.Content.Headers.ContentRange
    if ($range -and $range.Length) {
        $total = [int64]$range.Length
    } elseif ($remaining -ne $null) {
        $total = [int64]$remaining + $existing
    }

    $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $mode = if ($existing -gt 0) { [System.IO.FileMode]::Append } else { [System.IO.FileMode]::Create }
    $outputStream = [System.IO.FileStream]::new($temp, $mode, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)

    try {
        $buffer = New-Object byte[] (1024 * 1024)
        $done = $existing
        $lastDone = $done
        $lastTick = Get-Date
        while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $outputStream.Write($buffer, 0, $read)
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
        $outputStream.Dispose()
        $inputStream.Dispose()
        $response.Dispose()
        $client.Dispose()
        $handler.Dispose()
    }

    $finalSize = [int64](Get-Item -LiteralPath $temp).Length
    if ($total -and $finalSize -lt $total) {
        throw "$Label download incomplete: $(Format-Size $finalSize) / $(Format-Size $total)"
    }
    Move-Item -LiteralPath $temp -Destination $Destination -Force
    Write-Host "$Label download complete: $(Format-Size $finalSize)"
}

# A file is taken only with its pinned SHA-256: a wrong one is deleted and fetched once more.
function Get-PinnedFile {
    param([string]$Url, [string]$Destination, [string]$Sha, [string]$Label)
    if ((Test-Path -LiteralPath $Destination) -and (Get-Sha256 $Destination) -eq $Sha) {
        Write-Host "$Label already downloaded and verified."
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

function Test-PackInPlace {
    if (-not (Test-Path -LiteralPath $Marker)) { return $false }
    $said = @{}
    foreach ($line in Get-Content -LiteralPath $Marker -Encoding UTF8) {
        if ($line -match "^([a-z0-9_]+)=(.*)$") { $said[$Matches[1]] = $Matches[2].Trim() }
    }
    if ($said["version"] -ne $EngineVersion -or $said["sha256"] -ne $PackSha) { return $false }
    foreach ($name in @("transcribe.dll", "ggml.dll", "ggml-base.dll", "ggml-vulkan.dll", "contract.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $Tools $name))) { return $false }
    }
    return $true
}

Write-Host "Audion Voice Tools - the engine of dictation"
Write-Host "Engine: transcribe.cpp $EngineVersion (CPU + Vulkan) -> $Tools"
Write-Host "Model:  $ModelName -> $Models"
Write-Host ""

Write-Step 1 "transcribe.cpp $EngineVersion"
if (-not $Force -and (Test-PackInPlace)) {
    Write-Host "The engine is in place: $Tools"
} else {
    $pack = Join-Path $Download $PackName
    Get-PinnedFile -Url $PackUrl -Destination $pack -Sha $PackSha -Label $PackName

    Write-Step 2 "Unpack into Tools\transcribe"
    $unpack = Join-Path $Download "unpacked"
    if (Test-Path -LiteralPath $unpack) { Remove-Item -LiteralPath $unpack -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $unpack | Out-Null
    & tar.exe -xzf $pack -C $unpack
    if ($LASTEXITCODE -ne 0) { throw "tar could not unpack $PackName (exit code $LASTEXITCODE)." }
    $source = Join-Path $unpack $PackFolder
    if (-not (Test-Path -LiteralPath (Join-Path $source "transcribe.dll"))) { throw "$PackName holds no $PackFolder\transcribe.dll." }

    # The window holds transcribe.dll while it runs: a file in use is said in words, not as a stack trace.
    if (Test-Path -LiteralPath $Tools) {
        try {
            Remove-Item -LiteralPath $Tools -Recurse -Force
        } catch {
            throw "Tools\transcribe is in use - quit Audion Voice Tools from the tray and run the step again. ($($_.Exception.Message))"
        }
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Tools) | Out-Null
    Move-Item -LiteralPath $source -Destination $Tools
    Remove-Item -LiteralPath $unpack -Recurse -Force
    Set-Content -LiteralPath $Marker -Encoding UTF8 -Value @(
        "version=$EngineVersion",
        "kind=cpu-vulkan",
        "asset=$PackName",
        "sha256=$PackSha",
        "installed_at=$((Get-Date).ToString('s'))"
    )
    Write-Host "The engine is in place: $Tools"
}

if ($EngineOnly) {
    Write-Host "[OK] The engine of transcribe.cpp $EngineVersion is in place (the model of dictation not asked for)."
    return
}

Write-Step 3 "$ModelTitle (GGUF)"
if (-not $Force -and (Test-Path -LiteralPath $ModelPath) -and (Get-Item -LiteralPath $ModelPath).Length -eq $ModelBytes) {
    Write-Host "The model is in place: $ModelPath"
} else {
    Get-PinnedFile -Url "$ModelBase/$ModelName" -Destination $ModelPath -Sha $ModelSha -Label $ModelName
}
# The licence and the notice of the weights stay beside them.
foreach ($name in @("LICENSE", "NOTICE")) {
    $beside = Join-Path $Models "$($ModelRepo.Split('/')[1]).$name.txt"
    if ($Force -or -not (Test-Path -LiteralPath $beside)) {
        Download-FileWithProgress -Url "$ModelBase/$name" -Destination $beside -Label "model $name"
    }
}

Write-Step 4 "Verify"
if (-not (Test-PackInPlace)) { throw "The engine in $Tools is not whole after the install." }
if ((Get-Sha256 $ModelPath) -ne $ModelSha) { throw "The model $ModelPath does not match its SHA-256." }
Write-Host "[OK] The engine of dictation is ready: transcribe.cpp $EngineVersion, $ModelName."
