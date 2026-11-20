# Decisions and verification log

Every **Verify** item from the build plan, with what was checked, how, and the result.
Dates are when the check was made. "Open" means it still needs a person or a real Discord account.

## Toolchain (checked 2026-11-13)

| Item | Result | How |
| --- | --- | --- |
| .NET LTS | **.NET 10**, SDK 10.0.404 | `dotnet --list-sdks` after `winget install Microsoft.DotNet.SDK.10` |
| Windows App SDK | **2.6.5** (latest stable on NuGet) | NuGet flat-container index |
| CommunityToolkit.Mvvm | 8.4.2 | NuGet |
| Magick.NET | Magick.NET-Q8-AnyCPU **15.1.0** | NuGet |
| xUnit | 2.9.3 (template default), runner 3.1.4, Test SDK 17.14.1 | `dotnet new xunit` |
| Windows SDK build tools | 10.0.28000.2957 | NuGet |
| WinUI 3 unpackaged + self-contained builds with plain `dotnet build` | **Works** | `Squish.App` builds and launches with `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true` |
| Clean Windows 11 machine, no .NET installed | **Open** | Needs a clean VM. The publish is self-contained (no runtime install) but this has not been run on a machine without .NET. |

## FFmpeg build (checked 2026-11-13)

Chosen: **gyan.dev "release essentials" 9.0.2**, unmodified, static.

| File | SHA-256 | Size |
| --- | --- | --- |
| ffmpeg.exe | `3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC` | 105,423,872 |
| ffprobe.exe | `F0D36ECBBDD3BCFAC3EFA078C96C7271C2E68B3810595552AC3B7F17E9A65C52` | 105,221,120 |
| Source zip `ffmpeg-release-essentials.zip` | `60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA` | 114,768,076 |

`-buildconf` / `-encoders` / `-filters` confirm every required component:

- Required: libx264 ✔, native AAC ✔, libwebp ✔, zimg / `zscale` ✔, `tonemap` ✔, `bwdif` ✔, `palettegen`/`paletteuse` ✔,
  NVENC ✔ (`h264_nvenc`), Quick Sync ✔ (`h264_qsv`, via libvpl), AMF ✔ (`h264_amf`).
- Optional: libx265 ✔. **SVT-AV1 ✘** (not in essentials; libaom-av1 is). AV1 stays opt-in and unshipped until WP0 Discord tests anyway.
- Bench extra: libvmaf ✔ (so the bench and the shipped build can be the same binary).
- Licence: `--enable-gpl --enable-version3` → GPLv3. Squish runs it as a separate process; it is not linked.

**Size: target not met.** The two static executables are 210 MB together. The release zip is **125 MB**
(327 MB unpacked), against the plan's 80 MB download target. About 80 MB of the zip is FFmpeg. Zip compresses
each file on its own, so it can't exploit the fact that ffmpeg.exe and ffprobe.exe share most of their code.
Options, not yet tried: an FFmpeg "shared" build (both executables use one set of DLLs), or an installer with a
large-dictionary solid LZMA archive (Inno Setup / 7-Zip).

`scripts/fetch-ffmpeg.ps1` downloads the zip and checks the hash above.

## Hardware encoders on the dev machine

AMD Ryzen 7 5800H with Radeon Graphics (driver 31.0.21921.1000), 16 threads.
One-second test encodes: `h264_amf` **works**; `h264_nvenc` fails (no `nvcuda.dll`); `h264_qsv` fails (no MFX session).
So Fast mode was exercised with AMF only. NVENC and Quick Sync paths are untested on real hardware.

## Behaviour decisions made during the build

| Decision | Reason |
| --- | --- |
| Ladder rungs use the **short side** of the displayed picture, not the height | A vertical 1080×1920 phone clip is "1080p"; using height would treat it as 1920p and pick the wrong rung. |
| Source sizes between ladder rungs (e.g. 1280×800) are tried at their own size first | "Never upscale" plus "largest rung that passes" would otherwise skip straight to 720. |
| Re-encodes never exceed 1.5× the source video bitrate (1.0× under 3 s) | Avoids inflating already-small but non-MP4 sources (e.g. a VP9 WebM under the target). The plan's under-3 s rule is kept exactly. |
| Kind detection: magic bytes for images (Magick.NET), ffprobe for everything else | ffprobe mis-describes HEIC (tiled HEVC in a MOV box). Never uses the extension. |
| Still images are measured in memory before writing | Encoded byte count equals file size, so the quality search needs no temp files. |
| Images fit against 97% of the target, like video | The same margin keeps us safe while Discord's byte ceiling is unverified. |
| `IVerifier` became `CheckAsync` | The playability check runs ffprobe; a blocking call would hold a thread pool thread. |
| `CompressionRequest.OutputPath` became `OutputDirectory` | The extension, and so the final name, is only known after planning (PNG may become WebP). |
| Retry state (attempt, retries on rung, raised) lives on `VideoPlan` | Keeps the verifier a pure function of (plan, measured size), which the unit tests cover. |
| A bitrate raise that overshoots falls back to the earlier passing file | The plan only says "raise once"; this guarantees the raise can never turn a pass into a failure. |
| Hardware encodes that miss twice fall back to libx264 | Plan risk table: "fall back to software after two misses". |
| HEVC and AV1 are wired in Core but not offered in the UI | Plan: only after WP0 confirms inline playback. |
| Pass-through copies previewable images unchanged, including EXIF | Plan step 1 ("copy it unchanged") is followed literally. Only re-encoded images have GPS/EXIF stripped; the UI says "from images it compresses". |
| No "Check for updates" link | Personal use (open question 2); there is no releases page. |
| Licence for Squish's own code: **GPL-3.0-or-later** | Open question 3. Matches the bundled GPLv3 FFmpeg build; simplest if it is ever shared. |
| Trim stays in v1.1 | Open question 5. The size engine and its acceptance criteria come first; trim adds a timeline control and another package. |

## Changes after measurement and review (2026-11-19)

| Decision | Reason |
| --- | --- |
| Under-use raise threshold 0.90 → **0.93** (`Tuning.UnderuseFraction`) | AMF Fast-mode encodes landed at 92.1%, inside the plan's "no raise" band but below the 93% acceptance floor. The threshold now matches the acceptance window. |
| A rung is dropped after the first encode **and both retries** miss (three misses) | Plan text: "After two failed retries, drop one ladder rung". The reviewer read it as two misses; we keep the literal reading. One config value (`RetriesPerRung`) changes it. |
| Retry rung drops follow the planner's fps rule | Dropping from 720p60 now goes to 720p30 first, then 540p30, not straight to 540p60. |
| Sources already below 720p keep their frame rate at source size when the bitrate allows | The halve-before-dropping rule is about not going below 720p; it shouldn't halve a 480p60 clip that has room. |
| MP4s are copied untouched only when their audio is AAC | Opus/AC3/FLAC in MP4 may not play in Discord's player; those are re-encoded. |
| WebP candidates are capped at 16,383 px on the long edge | libwebp's hard limit. A 20,000 px panorama now downsizes and fits instead of failing. |
| Two-pass logs and x265 stats go to the user temp folder when the output folder is over 240 characters | x264 opens its stats files without long-path support, and Windows can't start a process in a working directory over MAX_PATH. Both are still cleaned up. |
| WinUI component packages (WinUI + Foundation) instead of the `Microsoft.WindowsAppSDK` metapackage | The metapackage ships AI/ML/Search/Widgets runtimes (onnxruntime, DirectML, ~60 MB) that Squish doesn't use. |
| Release builds are trimmed (`TrimMode=partial`) and use source-generated JSON and source-generated COM | Cuts the 55 MB Windows SDK projection. Trimming turns off reflection JSON and built-in COM, which broke settings and taskbar progress until replaced. |

## Still open (needs Eric or a Discord account)

- [ ] **Discord byte ceiling.** Upload 19.9 MiB, 20.0 MiB and 20.0 MB (decimal) files on a free account. Until then 1 MB = 1,000,000 bytes and the 97% aim stay.
- [ ] **Inline playback** of H.264, HEVC, AV1 and VP9 on desktop, web, iOS and Android.
- [ ] **Animated WebP autoplay** on all clients (decides whether animated WebP should replace GIF as default).
- [ ] **Attachment count per message** (UI assumes 10).
- [ ] **Boosted server (100 MB) and Nitro (500 MB vs 1 GB)** limits. Presets are in `presets.json`, so a change needs no rebuild.
- [ ] **HEIC decoding.** Magick.NET decoded AVIF in the corpus; no HEIC sample could be generated locally. Drop an iPhone photo in to confirm.
- [ ] **Clean-machine install** (no .NET) on a Windows 11 VM.
- [ ] **Native AOT / trimming for WinUI 3.** Not attempted; start time measured without it (see `docs/verification.md`).
