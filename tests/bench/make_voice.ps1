# The phrases of the measure spoken by the voices of Windows: Microsoft Irina for the Russian, Microsoft Zira for the
# English terms within it - WAV, 16 kHz mono PCM16, one per phrase, <id>.wav in OutDir. A set to try the measure and
# its clouds on: it says how fast, not how well an engine hears a person. Windows PowerShell 5.1 (System.Speech).
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\bench\make_voice.ps1 -OutDir ._runtime\bench\voice
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Phrases = ''
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 has no $PSScriptRoot in the defaults of param.
if (-not $Phrases) { $Phrases = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'phrases.json' }
Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force $OutDir | Out-Null
$set = Get-Content -Raw -Encoding UTF8 $Phrases | ConvertFrom-Json
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)

function Escape([string]$text) { [System.Security.SecurityElement]::Escape($text) }

foreach ($phrase in $set.phrases) {
    # Runs of Latin words go to the English voice, the rest to the Russian one.
    $body = ''
    $at = 0
    foreach ($m in [regex]::Matches($phrase.text, '[A-Za-z][A-Za-z0-9\-]*(?:\s+[A-Za-z][A-Za-z0-9\-]*)*')) {
        if ($m.Index -gt $at) { $body += '<voice name="Microsoft Irina Desktop">' + (Escape $phrase.text.Substring($at, $m.Index - $at)) + '</voice>' }
        $body += '<voice name="Microsoft Zira Desktop">' + (Escape $m.Value) + '</voice>'
        $at = $m.Index + $m.Length
    }
    if ($at -lt $phrase.text.Length) { $body += '<voice name="Microsoft Irina Desktop">' + (Escape $phrase.text.Substring($at)) + '</voice>' }
    $ssml = '<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="ru-RU"><break time="400ms"/>' + $body + '<break time="400ms"/></speak>'
    $path = Join-Path $OutDir ($phrase.id + '.wav')
    $voice = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $voice.SetOutputToWaveFile($path, $format)
    $voice.SpeakSsml($ssml)
    $voice.Dispose()
    Write-Output ('{0} {1:0.0} s' -f $phrase.id, (((Get-Item $path).Length - 44) / 32000))
}
