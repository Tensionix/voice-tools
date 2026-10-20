# Audion Voice AI Live

<!-- audion:release -->
<p align="center">
  <a href="https://audion.dev/downloads/voice-tools"><img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0b6db8?style=flat-square&logo=windows&logoColor=white"></a>
  <a href="https://github.com/Tensionix/voice-tools/releases/latest"><img alt="Release" src="https://img.shields.io/github/v/release/Tensionix/voice-tools?style=flat-square&label=release&color=2a7488"></a>
  <a href="https://github.com/Tensionix/voice-tools/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/Tensionix/voice-tools/total?style=flat-square&label=downloads&color=5fd08a"></a>
  <a href="https://github.com/Tensionix/voice-tools/blob/main/LICENSE"><img alt="License" src="https://img.shields.io/github/license/Tensionix/voice-tools?style=flat-square&color=5fd08a&logo=apache&logoColor=white&cacheSeconds=3600"></a>
</p>

**Version 3.1.0** · 2026-10-20 · 497.6 MB

- [Direct download](https://dl.audion.dev/voice-tools/3.1.0/Audion_Voice_Tools_v3.1.0_Full.zip) — unmetered, no rate limits
- [Project page](https://audion.dev/downloads/voice-tools) — every version and how to install
- [GitHub release](https://github.com/Tensionix/voice-tools/releases/tag/v3.1.0)

<p align="center"><img src="Docs/screenshot.png" alt="The program window" width="560"></p>

`SHA-256: 5ae30787e78e886fc355afe48849c5cdf03d176892bd5e631687396ed65a0d50`

---

An **Audion** tool, published by [Tensionix](https://github.com/Tensionix).
<!-- /audion:release -->


[Русский](Docs/README_RU.md) · [User Guide](Docs/USER_GUIDE_EN.md)

**Contents**

- [Why It Exists](#why-it-exists)
- [What Has to Be Installed Separately](#what-has-to-be-installed-separately)
- [Editions](#editions)
- [The Principle](#the-principle)
- [What It Can Do](#what-it-can-do)
- [Next](#next)
- [Technical Reference](#technical-reference)
  - [FFmpeg and the NVIDIA Driver](#ffmpeg-and-the-nvidia-driver)
  - [Portability](#portability)

Transcribing recordings, live dictation, cleaning up text, and exporting working
notes. The light edition — for a laptop and everyday work.

## Why It Exists

Transcription is needed in two entirely different situations.

**Quickly, right now.** Dictate a paragraph, transcribe a ten-minute recording,
get the text immediately. Response time is what matters, and a cloud service fits
best.

**In bulk, offline.** An hour-long meeting, a whole day of dictaphone recording, a
folder of files. Here autonomy matters more: the recording should not leave the
machine, and the cost should not grow with every hour.

The program does both, and switching between them is a matter of choosing an
engine, not a different program.

## What Has to Be Installed Separately

**Model weights are not part of the distribution** — together they come to about
3.3 GB. The weights carry their own licences, separate from the program's,
so the user downloads them under their own account where that is required.

Until the models are installed, transcription will not start: the window opens,
but there is nothing to recognise with.

**On the first start the app offers to download what is missing.** The
"Download models and engines" window lists the modules with their download
size, for Live that is GigaAM and whisper.cpp with the Turbo model, about
3.3 GB. "Download and install" installs them one after another; progress is
shown on the Maintenance page, and once everything is in place every mode
there is green. "Later" postpones the question until the next start, "Don't
ask again" hides the window for good. Modules can still be installed by hand
on the same page.

## Editions

| edition | for what | engines |
|---|---|---|
| **Live** | laptop, everyday work | cloud services, GigaAM, whisper.cpp, speaker separation with a known speaker count |
| Studio | workstation with an NVIDIA card | the same plus CUDA and automatic speaker separation |

Live is the main version. Studio adds heavy local engines and speaker separation
for machines with the hardware to run them.

## The Principle

**Reliable first, elegant second.** Batch transcription behaves predictably, and
everything else rests on it. An overlay on top of other windows and instant
on-the-fly parsing were both tried — and both broke the window itself. Heavy work
is not hung on the thread that draws the interface.

## What It Can Do

Transcribing files and folders, live dictation, cleaning up transcribed text,
exporting into working notes, a tray icon for quick access, resetting the
application to its initial state.

## Next

* [User Guide](Docs/USER_GUIDE_EN.md) — step by step, engines, formats.

---

## Technical Reference

### FFmpeg and the NVIDIA Driver

Every FFmpeg build is compiled against a particular version of the
hardware-encoding headers, and each demands its own minimum driver. The newest
build on an old driver does not accelerate anything — it breaks the hardware path.
So the build is chosen to match the driver, not by version number.

### Portability

Application state lives in the project folder, not in the system. A separate
command returns the application to its initial state without touching the
downloaded models.
