[CmdletBinding()]
param(
    [ValidateSet("auto", "cpu", "cublas")]
    [string]$Pack = "auto",
    [switch]$SkipModel,
    [switch]$Force,
    # SETUP of the window: put the newest release in place - a pack left in install\download is not reused.
    [switch]$Update
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$InstallDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $PSCommandPath }
$Root = (Resolve-Path (Join-Path $InstallDir "..")).Path
$Tools = Join-Path $Root "Tools\whispercpp"
$Models = Join-Path $Root "models"
$Download = Join-Path $InstallDir "download\whispercpp"
$Unpack = Join-Path $Download "unpacked"
$PackMarker = Join-Path $Tools "audion-whispercpp-pack.txt"
$TurboModel = Join-Path $Models "ggml-large-v3-turbo.bin"
$TurboUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin"
$BinaryNames = @(
    "whisper-server.exe",
    "server.exe",
    "whisper-cli.exe",
    "main.exe",
    "whisper.exe"
)

function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) {
        return ("{0:N2} GB" -f ($Bytes / 1GB))
    }
    if ($Bytes -ge 1MB) {
        return ("{0:N1} MB" -f ($Bytes / 1MB))
    }
    if ($Bytes -ge 1KB) {
        return ("{0:N1} KB" -f ($Bytes / 1KB))
    }
    return ("{0:N0} B" -f $Bytes)
}

function Download-FileWithProgress {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (Test-Path -LiteralPath $Destination) {
        $existingDone = (Get-Item -LiteralPath $Destination).Length
        if ($existingDone -gt 0) {
            Write-Host "$Label already exists: $Destination ($(Format-Size $existingDone))"
            return
        }
    }

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
        Write-Host "$Label download started..."
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
    $outputStream = [System.IO.FileStream]::new(
        $temp,
        $mode,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::Read
    )

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
    Write-Host "$Label download complete: $Destination ($(Format-Size $finalSize))"
}

function Find-WhisperCppBinary {
    param([string]$RootDir)
    if (-not (Test-Path -LiteralPath $RootDir)) {
        return $null
    }
    Get-ChildItem -LiteralPath $RootDir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $BinaryNames -contains $_.Name.ToLowerInvariant() } |
        Select-Object -First 1
}

function Get-LocalPack {
    param([string]$DesiredKind)
    if (-not (Test-Path -LiteralPath $Download)) {
        return $null
    }
    # Only a pack of the kind asked for: the CPU pack left by the last install is no CUDA pack.
    Get-ChildItem -LiteralPath $Download -Filter "*.zip" -File -ErrorAction SilentlyContinue |
        Where-Object { (Get-PackKindFromName $_.Name) -eq $DesiredKind } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Get-ProjectEdition {
    $settings = Join-Path $Root "config\app_settings.yaml"
    if (-not (Test-Path -LiteralPath $settings)) {
        return ""
    }
    try {
        $line = Get-Content -LiteralPath $settings -ErrorAction Stop |
            Where-Object { $_ -match "^\s*edition\s*:" } |
            Select-Object -First 1
        if ($line) {
            return (($line -replace "^\s*edition\s*:\s*", "") -replace "['""]", "").Trim().ToLowerInvariant()
        }
    } catch {
    }
    return ""
}

function Resolve-DesiredPackKind {
    if ($Pack -ne "auto") {
        return $Pack
    }
    $edition = Get-ProjectEdition
    if ($edition -eq "studio") {
        return "cublas"
    }
    return "cpu"
}

function Get-PackKindFromName {
    param([string]$Name)
    if ($Name -match "(?i)(cublas|cuda)") {
        return "cublas"
    }
    if ($Name -match "(?i)vulkan") {
        return "manual"
    }
    if ($Name -match "(?i)(cpu|whisper-bin-x64)") {
        return "cpu"
    }
    return "manual"
}

# whisper.cpp stopped attaching its Windows packs to the versioned releases from v1.9.3 on: the packs come with the
# builds (tags bNNNN), many of them pre-releases. The newest release that is no pre-release and carries the pack is
# taken; v1.9.2 - the last versioned one that carries it - when GitHub cannot be asked.
$FallbackTag = "v1.9.2"

function Test-AssetUrl {
    param([string]$Url, [hashtable]$Headers)
    try {
        $null = Invoke-WebRequest -Uri $Url -Method Head -Headers $Headers -TimeoutSec 30 -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

function Get-ReleasePack {
    param([Parameter(Mandatory = $true)][string]$DesiredKind)

    $repo = "https://github.com/ggml-org/whisper.cpp"
    $api = "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=20"
    $headers = @{ "User-Agent" = "Audion-Voice-Tools" }

    $assetName = if ($DesiredKind -eq "cublas") {
        "whisper-cublas-12.4.0-bin-x64.zip"
    } else {
        "whisper-bin-x64.zip"
    }

    Write-Host "Querying whisper.cpp releases for $assetName..."
    try {
        $releases = Invoke-RestMethod -Uri $api -Headers $headers -TimeoutSec 30 -ErrorAction Stop
        foreach ($release in @($releases)) {
            if ($release.draft -or $release.prerelease) {
                continue
            }
            $asset = $release.assets |
                Where-Object { $_.name -eq $assetName } |
                Select-Object -First 1
            if ($asset) {
                Write-Host "[INFO] The newest release with $assetName`: $($release.tag_name)"
                return [pscustomobject]@{ Asset = $asset; Kind = $DesiredKind; Tag = $release.tag_name }
            }
        }
        Write-Host "[WARN] No recent release lists $assetName; trying $FallbackTag."
    } catch {
        Write-Host "[WARN] GitHub API unavailable ($($_.Exception.Message)); trying $FallbackTag."
    }

    $downloadUrl = "$repo/releases/download/$FallbackTag/$assetName"
    if (-not (Test-AssetUrl -Url $downloadUrl -Headers $headers)) {
        throw "whisper.cpp $FallbackTag carries no $assetName either: $downloadUrl"
    }
    $asset = [pscustomobject]@{
        name = $assetName
        browser_download_url = $downloadUrl
    }
    Write-Host "[INFO] Resolved to $FallbackTag."
    return [pscustomobject]@{ Asset = $asset; Kind = $DesiredKind; Tag = $FallbackTag }
}

function Get-InstalledPackKind {
    if (-not (Test-Path -LiteralPath $PackMarker)) {
        return ""
    }
    try {
        $line = Get-Content -LiteralPath $PackMarker -ErrorAction Stop |
            Where-Object { $_ -match "^kind=" } |
            Select-Object -First 1
        if ($line) {
            return ($line -replace "^kind=", "").Trim()
        }
    } catch {
    }
    return ""
}

function Expand-Pack {
    param([string]$ZipPath)
    if (Test-Path -LiteralPath $Unpack) {
        Remove-Item -LiteralPath $Unpack -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Unpack | Out-Null
    Expand-Archive -Path $ZipPath -DestinationPath $Unpack -Force

    $children = @(Get-ChildItem -LiteralPath $Unpack -Force)
    $source = $Unpack
    if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
        $source = $children[0].FullName
    }
    if (Test-Path -LiteralPath $Tools) {
        Get-ChildItem -LiteralPath $Tools -Force | Remove-Item -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Tools | Out-Null
    Copy-Item -Path (Join-Path $source "*") -Destination $Tools -Recurse -Force
}

Write-Host "[Audion Voice Tools] Installing whisper.cpp local pack..."
Write-Host "Root: $Root"
Write-Host ""

New-Item -ItemType Directory -Force -Path $Tools, $Models, $Download | Out-Null

$desiredKind = Resolve-DesiredPackKind
Write-Host "Desired pack kind: $desiredKind"

$localPack = if ($Update) { $null } else { Get-LocalPack -DesiredKind $desiredKind }
$installedKind = Get-InstalledPackKind
$binary = Find-WhisperCppBinary $Tools
$packReady = $binary -and ($installedKind -eq $desiredKind)
if ($packReady -and -not $localPack -and -not $Force -and -not $Update) {
    Write-Host "whisper.cpp binary already present: $($binary.FullName)"
    Write-Host "Installed pack kind: $installedKind"
} else {
    if ($binary -and -not $localPack) {
        $reason = if ($Update) { "update to the newest release" } elseif ($Force) { "forced reinstall" } else { "installed kind '$installedKind' does not match '$desiredKind'" }
        Write-Host "Reinstalling whisper.cpp pack: $reason"
    }

    $packKind = "manual"
    $packName = ""
    $packTag = ""
    if ($localPack) {
        Write-Host "Using local whisper.cpp pack: $($localPack.FullName)"
        $packName = $localPack.Name
        $packKind = Get-PackKindFromName $localPack.Name
        Expand-Pack $localPack.FullName
    } else {
        $releasePack = Get-ReleasePack -DesiredKind $desiredKind
        if (-not $releasePack) {
            Write-Host ""
            Write-Host "Required Windows x64 whisper.cpp archive was not found in the latest release."
            Write-Host "Expected pack kind: $desiredKind"
            Write-Host "Place a matching ZIP into:"
            Write-Host "  $Download"
            Write-Host "Then rerun install\Install-Live-Vulkan.cmd."
            exit 2
        }

        $asset = $releasePack.Asset
        $packKind = $releasePack.Kind
        $packName = $asset.name
        $packTag = $releasePack.Tag
        Write-Host "Installing official $packKind pack: $($asset.name)"
        $zip = Join-Path $Download $asset.name
        Download-FileWithProgress -Url $asset.browser_download_url -Destination $zip -Label $asset.name
        Expand-Pack $zip
    }

    $binary = Find-WhisperCppBinary $Tools
    if (-not $binary) {
        throw "Pack unpacked, but no whisper.cpp executable was found under $Tools."
    }
    Set-Content -LiteralPath $PackMarker -Encoding UTF8 -Value @(
        "kind=$packKind",
        "asset=$packName",
        "tag=$packTag",
        "installed_at=$((Get-Date).ToString('s'))"
    )
}

# The model goes with the pack. The CPU pack: turbo, the one Whisper a processor carries. The CUDA pack: large-v2 alone
# (Install-WhisperCpp-Large-Models.cmd) - turbo loops on CUDA, so it is not downloaded, and one left from the CPU pack
# goes to the Recycle Bin.
$LargeV2Model = Join-Path $Models "ggml-large-v2.bin"
if ((Get-InstalledPackKind) -eq "cublas") {
    if (Test-Path -LiteralPath $TurboModel) {
        try {
            # The Recycle Bin of the shell: 0x14 - no question, no progress window; the move ends on its own thread.
            (New-Object -ComObject Shell.Application).Namespace(10).MoveHere($TurboModel, 0x14)
            for ($wait = 0; $wait -lt 40 -and (Test-Path -LiteralPath $TurboModel); $wait++) { Start-Sleep -Milliseconds 250 }
        } catch {
        }
        if (Test-Path -LiteralPath $TurboModel) {
            Write-Warning "Turbo model stays in $TurboModel - it is not used on CUDA: move it to the Recycle Bin to free 1.5 GB."
        } else {
            Write-Host "Turbo model went to the Recycle Bin: it is not used on CUDA (it repeats phrases there)."
        }
    } else {
        Write-Host "Turbo model: not used on CUDA, not downloaded."
    }
    $RequiredModel = $LargeV2Model
    $ModelHint = "install\Install-WhisperCpp-Large-Models.cmd"
} else {
    if (-not $SkipModel -and -not (Test-Path -LiteralPath $TurboModel)) {
        Download-FileWithProgress -Url $TurboUrl -Destination $TurboModel -Label "Turbo model"
    } elseif (Test-Path -LiteralPath $TurboModel) {
        Write-Host "Turbo model already present."
    } else {
        Write-Host "Turbo model skipped by request."
    }
    $RequiredModel = $TurboModel
    $ModelHint = "install\Install-Live-Vulkan.cmd"
}

Write-Host "Installed binary: $($binary.FullName)"
if (Test-Path -LiteralPath $PackMarker) {
    Write-Host "Installed pack kind: $(Get-InstalledPackKind)"
}
if (Test-Path -LiteralPath $RequiredModel) {
    Write-Host "Required model: $RequiredModel"
} else {
    Write-Host "Required model: $RequiredModel - missing, $ModelHint puts it in place"
}
