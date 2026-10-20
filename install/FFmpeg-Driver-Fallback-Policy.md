# FFmpeg by the NVIDIA driver

Since 18.10.2026 (the owner: the 610 series of NVIDIA drivers has settled, 610.88 is its LTS "golden" driver; the programs
go over to FFmpeg 9.x, chosen by the driver, with a warning where the driver is older; and the newest stable is taken, not
a pinned one):

| NVIDIA driver | Installed |
|---|---|
| no NVIDIA card | the newest Gyan stable, 9.0.2 at least |
| 610.88 (LTS) or newer | the newest Gyan stable, 9.0.2 at least; NVENC tested on one frame |
| 570.0 - 610.87 | 8.0.1 (NVENC API 13.0) and a `[WARN]`: not every operation is possible |
| 551.76 - 569.xx | 7.1 (NVENC SDK 11.1) and the same `[WARN]` |
| below 551.76 | stop: no automatic NVENC branch is defined |
| NVIDIA card, driver version unknown | stop: compatibility cannot be verified |

- **9.0.2 is the floor**: the build the programs are checked with on the LTS driver. The newest stable is never taken
  below it (`$FloorRelease` of `Install-Portable-FFmpeg-Gyan.ps1`).
- **A newer build that fails NVENC goes back to the floor.** A new FFmpeg may be built with NVENC headers that ask a
  driver newer than 610.88; when its one-frame NVENC test fails, `Install-Portable-FFmpeg-Gyan.cmd` installs 9.0.2 once
  and tests again. 8.0.1, 7.1 and the floor itself do not go back.
- **Below 610.88** the older branch keeps NVENC working; what needs FFmpeg 9.x is not there. The installer says so in a
  `[WARN]` line.

The rule lives in `install\Install-Portable-FFmpeg-Gyan.ps1` (it installs) and in this file; Audion VS Engine and Audion
Media Tools hold the same.

Master, nightly and autobuild assets are never selected. BtbN publishes rolling release-branch heads, not exact stable
tags: by default its installer hands over to Gyan (exit code 20); its emergency mode (`-AllowRollingBranch`) follows the
same driver gate with the newest series from 610.88.

Manual Gyan version override:

    Install-Portable-FFmpeg-Gyan.cmd /VERSION 9.0.2
    Install-Portable-FFmpeg-Gyan.cmd /VERSION 8.0.1
    Install-Portable-FFmpeg-Gyan.cmd /VERSION 7.1

Files must remain together in the same install script directory.

## Why the driver decides

Every FFmpeg build is compiled against its NVENC headers and demands their minimum driver:

| FFmpeg | NVENC headers | Minimum driver |
|---|---|---|
| 9.0.x | ffnvcodec n13.1.15.0 | 610.0 |
| 8.1.x | ffnvcodec n13.1.x | 610.0 |
| 8.0.1 | ffnvcodec n13.0.19.0 | 570.0 |
| 7.1.1 | ffnvcodec n13.0.19.0 | 570.0 |
| 7.1 | ffnvcodec n12.2.72.0 | 551.76 |

The newest build on an old driver does not accelerate anything - it breaks the hardware path. 7.1.1 does not help an old
driver: it was built with the headers of 8.0.1 and needs the same 570.0. The programs ask 610.88, not the 610.0 of the
headers: it is the LTS driver of the series, the one they are checked with.
