# Squish

A Windows 11 app that shrinks videos, images and GIFs to fit under a Discord upload limit, with as little
visible quality loss as it can manage. Fully offline.

Drop files in, pick a size (20 MB by default, the free limit), press **Compress**, then **Copy all** and paste
into Discord. Results are saved beside the originals as `<name>_discord.<ext>`.

## Layout

| Path | What it is |
| --- | --- |
| `src/Squish.Core` | Cross-platform engine: probe, planners, encoders, verifier, job queue, presets, settings |
| `src/Squish.App` | WinUI 3 app (Windows App SDK, unpackaged, self-contained) |
| `src/Squish.Cli` | Command line over Core: `squish --target 20 file.mp4` |
| `tests/Squish.Core.Tests` | xUnit tests for planners, verifier, probe parsing, sniffing, presets |
| `bench/Squish.Bench` | Runs the corpus at 10/20/50 MB; reports size accuracy, VMAF/SSIM, speed |
| `scripts/` | FFmpeg fetch, corpus generator, publish, installer, icon |
| `docs/decisions.md` | Every Verify item from the plan and its result |
| `docs/verification.md` | What was measured for the acceptance criteria, and what wasn't |

## Build

Needs the .NET 10 SDK on Windows 11.

```powershell
powershell -File scripts\fetch-ffmpeg.ps1     # FFmpeg 9.0.2 essentials into tools\ffmpeg, hash-checked
dotnet build Squish.slnx
dotnet test tests\Squish.Core.Tests
dotnet run --project src\Squish.App            # or: Squish.exe --demo  for every card state with fake data
```

## Test corpus and bench

```powershell
powershell -File scripts\make-corpus.ps1      # synthetic clips/images/GIFs from FFmpeg test sources (~10 GB)
dotnet run --project bench\Squish.Bench -c Release -- --targets 10,20,50
```

Results land in `bench-results\<timestamp>\results.csv` and `report.txt`.

## Release

```powershell
powershell -File scripts\publish.ps1
```

Produces `artifacts\Squish-<version>-win-x64.zip`. Unzip anywhere and run `Squish.exe` (portable), or run
`install.cmd` inside it for a per-user install with a Start menu entry and a **Send to → Compress for Discord**
entry. No admin rights. Uninstall from Settings → Apps.

## Licence

GPL-3.0-or-later. Bundles an unmodified GPLv3 FFmpeg build, run as a separate program. See
`THIRD-PARTY-NOTICES.txt`.
