# Verification against plan section 13

What was measured on the dev machine (Ryzen 7 5800H, 16 threads, Radeon iGPU, Windows 11), and what was not.
"Not verified" means nobody has checked it yet; it does not mean it fails.

## Automated tests

`dotnet test tests\Squish.Core.Tests`: **97 passed, 0 failed** (2026-11-19).

- Unit: every ladder rung, every audio tier and boundary, fps halving (including sub-720p sources), too-long flag,
  vertical and rotated sources, short-clip cap, HDR/interlace flags, Fast-mode aim, retry maths, rung drops,
  hardware fallback, raise-once, bounded attempts, image format choice, GIF scale grid, presets, settings, ffprobe
  JSON parsing (rotation, HDR, VFR, audio-only, missing duration), magic-number sniffing (PNG/APNG/alpha, JPEG, GIF,
  HEIF, AVIF, TIFF).
- Integration (real FFmpeg): size window, cancel (< 1 s, nothing left behind), zero-byte / text / truncated files,
  emoji + non-Latin paths, input and output paths over 260 characters, name collisions, copy-untouched, unwritable
  output folder, PNG with alpha → WebP, 20,000 px panorama, cancelled capability check.

## End-to-end in the release build

`scripts\e2e-ui.ps1` drives the trimmed, self-contained release through UI Automation (the tree Narrator reads):
launch with files → Compress → wait for "Copy all" → press it → read the clipboard.

| Check | Result |
| --- | --- |
| Video (WebM 50 MB) | 19.3 MB (96.4%) |
| Photo (24 MP JPEG, 23.3 MB) | 18.5 MB, quality 92 |
| GIF (86.9 MB) | 16.6 MB |
| Text file named .mp4 | Failed with "This isn't a video or image Squish can read…"; the queue continued |
| Copy all | 3 files on the clipboard as a file-drop list (CF_HDROP), the format Discord reads on paste |
| Temp files after the run | None |
| Second launch with a file (what "Send to" does) | Second process exits (code 0); the file appears in the first window |
| Card states | Every state shown in `Squish.exe --demo`; card automation names carry the result text |

## Budgets (plan section 10)

| Measure | Budget | Measured |
| --- | --- | --- |
| Cold start to window | < 1.5 s | 0.57–1.38 s (release build, includes the machine being busy) |
| Memory with a few files queued | < 150 MB idle | 113–139 MB working set |
| Cancel to process exit | < 1 s | Passes the integration test on an idle machine; 1.2 s while the bench saturated all 16 threads |
| Idle CPU | 0% | Not measured yet |
| 50 files added in < 2 s | | Not measured |
| UI frame drops during encodes | none | Not measured; all work is off the UI thread by construction |

## Size accuracy, Best mode (CLI, 20 MB target)

| File | Result |
| --- | --- |
| 1080p HDR (PQ) | 96.7%, tone-mapped to BT.709 |
| 1080×1920 with rotation metadata | 96.6%, upright, no rotation tag |
| 5.1 audio | 96.1%, stereo AAC |
| Interlaced 1080i | 96.4%, progressive |
| Variable frame rate screen capture | 96.0% |
| AVI (MPEG-4 + MP3), WebM (VP9 + Opus), MKV | 96.2–96.4% |
| 2 s clip over the target | 97.7% (one retry) |
| Small MKV under the target | Remuxed to MP4, no re-encode |

All MP4 outputs have `moov` before `mdat` (faststart).

Fast mode (AMD AMF): 92.1–93.8% before the threshold change, 93.0–94.0% after; 6–25 s per file versus 30–90 s
for Best.

## Bench (full corpus × 10/20/50 MB)

See the newest `bench-results\<timestamp>\report.txt`. Summary is filled in below once the run finishes.

## Not verified

- Anything that needs Discord: real byte ceiling, inline playback on each client, paste-to-attach in the Discord
  app itself (the clipboard format was checked, Discord's reaction to it was not).
- Clean Windows 11 machine with no .NET installed.
- High-contrast theme and 150% / 300% scaling (changing system settings was out of scope; uses theme resources
  throughout, so expected to work).
- Keyboard-only run of the full flow. Accelerators exist (Ctrl+V, Ctrl+O, Delete, Enter in the list,
  Ctrl+Enter); the e2e run used UI Automation, not keystrokes.
- Narrator speaking the live-region announcements (the automation names were read back; speech was not heard).
- NVENC and Quick Sync (no NVIDIA or Intel GPU on this machine).
- HEIC input (no sample could be generated locally).
- Download size: 125 MB zip versus the 80 MB target (see `decisions.md`).
