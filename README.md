# Squish

Shrinks videos, images and GIFs to fit under a Discord upload limit, with as little visible quality loss as it
can manage. Nothing is uploaded: all compression happens on your own device.

There are two versions:

| | Where | Best for |
| --- | --- | --- |
| **Web app** | **https://squish.select** (installable, works offline) | Phones, or any computer with a modern browser |
| **Windows app** | Build from this repo (see below) | Big files, best quality: two-pass x264 via FFmpeg |

## Using it

1. Add files: drop them on the window, paste them, or pick them (Browse / Choose files).
2. Pick a size: 10, 20 (the free limit, default), 50, 100, 500 MB, or Custom.
3. Press **Compress**. Each file shows progress, then its before and after size.
4. Send it to Discord:
   - **Windows:** **Copy all**, then Ctrl+V in Discord. Results are also saved beside the originals as
     `<name>_discord.<ext>`.
   - **Phone:** **Share** → Discord, or **Save** to keep a copy.

To install the web app: Safari → Share → **Add to Home Screen** (iPhone), or Chrome → ⋮ → **Install app** (Android).
It works on Chrome/Edge and Safari on iOS 17 or later; Settings → This device shows what your browser supports.

What it does to each file:

- **Video** → MP4 (H.264 + AAC). Works out the bitrate that fills about 95% of the target, picks the best
  resolution and frame rate for that bitrate, encodes, measures the real file and retries if it misses.
- **Images** → keeps JPEG/WebP; PNG tries lossless first; HEIC/AVIF/TIFF/BMP become JPEG. Finds the highest
  quality that fits, then downsizes only if it has to. Location data (EXIF/GPS) is removed.
- **GIFs** → stay GIFs (smaller size, frame rate or colours as needed), or **Convert to video** for a much
  smaller, sharper MP4.
- Files that already fit are copied untouched (or repackaged as MP4 without re-encoding).

## Status

The release gate from the build plan is met on the test corpus (`docs/verification.md`):

- **0 of 84 bench runs over the target** at 10, 20 and 50 MB.
- **All 41 compressed videos used 95.0–97.9%** of the target (criterion: 93–99.5%).
- Every hostile file (empty, truncated, a text file renamed `.mp4`) fails with a plain message, and the queue
  carries on.
- Mean quality: VMAF 91.8 for video, SSIM 0.92 for images.
- 97 .NET tests and 21 web tests pass.

Still open: anything that needs a real Discord account (the exact byte limit, inline playback on each client),
a clean-machine install test, and the starting-value tuning sweep. See `docs/decisions.md`.

## Repository layout

| Path | What it is |
| --- | --- |
| `src/Squish.Core` | Cross-platform engine: probe, planners, encoders, verifier, job queue, presets, settings |
| `src/Squish.App` | WinUI 3 app (Windows App SDK, unpackaged, self-contained) |
| `src/Squish.Cli` | Command line over Core: `squish --target 20 file.mp4` |
| `tests/Squish.Core.Tests` | xUnit tests: planners, verifier, probe parsing, sniffing, presets, real-FFmpeg integration |
| `bench/Squish.Bench` | Runs the corpus at 10/20/50 MB; reports size accuracy, VMAF/SSIM, speed |
| `web/` | The web app: same planner ported to TypeScript, runs on WebCodecs. See [`web/README.md`](web/README.md) |
| `scripts/` | FFmpeg fetch, corpus generator, publish, installer, icons, UI end-to-end test |
| `docs/decisions.md` | Every Verify item from the plan, its result, and decisions made during the build |
| `docs/verification.md` | What was measured against the acceptance criteria, and what wasn't |

## Windows app

### Build and run

Needs the .NET 10 SDK on Windows 11.

```powershell
powershell -File scripts\fetch-ffmpeg.ps1     # FFmpeg 9.0.2 essentials into tools\ffmpeg, hash-checked
dotnet build Squish.slnx
dotnet test tests\Squish.Core.Tests
dotnet run --project src\Squish.App            # or: Squish.exe --demo  for every card state with fake data
```

### Release

```powershell
powershell -File scripts\publish.ps1
```

Produces `artifacts\Squish-<version>-win-x64.zip` (self-contained, about 125 MB, most of it FFmpeg). Unzip
anywhere and run `Squish.exe`, or run `install.cmd` inside it for a per-user install with a Start menu entry and a
**Send to → Compress for Discord** entry. No admin rights. Uninstall from Settings → Apps.

### Test corpus and bench

```powershell
powershell -File scripts\make-corpus.ps1      # synthetic clips/images/GIFs from FFmpeg test sources (~10 GB)
dotnet run --project bench\Squish.Bench -c Release -- --targets 10,20,50
```

Results land in `bench-results\<timestamp>\results.csv` and `report.txt` (both git-ignored).

## Web app

```powershell
cd web
npm install
npm run dev      # http://localhost:5173
npm test
```

**Deploying:** Vercel builds and publishes automatically on every push to `main` that changes `web/`, to
https://squish.select (also www.squish.select and squish-ruby.vercel.app). Pushes that only touch the Windows
app don't trigger a build. Details in [`web/README.md`](web/README.md).

## Licence

GPL-3.0-or-later. The Windows app bundles an unmodified GPLv3 FFmpeg build, run as a separate program. See
`THIRD-PARTY-NOTICES.txt`.
