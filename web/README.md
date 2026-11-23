# Squish web

The phone version of Squish: a website (and installable app) that shrinks videos, images and GIFs to fit a
Discord upload limit. Everything runs inside the browser on your device. Nothing is uploaded, so it can be
hosted on any static host.

## How it works

| Kind | Engine | Notes |
| --- | --- | --- |
| Video | [Mediabunny](https://mediabunny.dev) on WebCodecs: the device's own H.264 encoder | Same planner as the desktop app (bitrate budget, audio tiers, resolution ladder, bits-per-pixel floor). Single-pass, so it aims at 94% and retries with a corrected bitrate, like the desktop app's Fast mode. Output: MP4, H.264 + AAC, fast start, metadata removed, rotation baked in. |
| Images | The browser's canvas encoders | Lossless first for PNG, then a quality binary search, then Lanczos-style stepped downscaling. EXIF orientation applied, EXIF/GPS removed. WebP falls back to JPEG/PNG where the browser can't write WebP (Safari). |
| GIF / animated WebP / APNG | ImageDecoder + [gifenc](https://github.com/mattdesl/gifenc) | Searches size, then frame rate, then colours. "Convert to video" per file makes an MP4 instead. |

Finished files go to the phone's share sheet (pick Discord) or download with Save.

## Browser support

- **Chrome / Edge (Android, desktop):** everything.
- **Safari (iOS 17+, macOS):** videos and images. GIF compression needs ImageDecoder (recent Safari only).
- **Firefox:** images; video where WebCodecs H.264 is available. AAC is filled in by a WASM encoder.

The app checks what the browser can do (Settings → This device) and says so instead of failing silently.

## Develop

```bash
npm install
npm run dev        # http://localhost:5173
npm test           # planner, retry and sniffing tests (Vitest)
npm run build      # dist/; set BASE_PATH=/Squish/ for a GitHub Pages project site
```

## Deploy

- **GitHub Pages:** `.github/workflows/pages.yml` builds and deploys on every push to `main` that touches `web/`.
  Enable it once under the repo's Settings → Pages → Source: GitHub Actions. Pages on a private repo needs a
  paid GitHub plan.
- **Vercel:** import the GitHub repo, set **Root Directory** to `web`. `vercel.json` has the build settings.
