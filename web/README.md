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

Live at **https://squish.select** (also `www.squish.select` and `squish-ruby.vercel.app`).

- **Vercel (in use):** project `squish`, connected to `cloudec11/Squish`, Root Directory `web`, production
  branch `main`. Every push to `main` that changes `web/` builds and publishes; other pushes are skipped
  (ignore command `git diff --quiet HEAD^ HEAD ./`). Build settings are in `vercel.json`.
  Deploy by pushing; a manual `vercel deploy` from this folder no longer works now that the project's root is
  `web`, and running it from the repo root would upload the local test corpus.
- **Domain:** registered at Porkbun, using Porkbun's nameservers. DNS: two `A` records for `@`
  (`216.198.79.1`, `64.29.17.1`) and a `CNAME` for `www` pointing at Vercel. Vercel issues the HTTPS certificate.
- **GitHub Pages (not in use):** `.github/workflows/pages.yml` can still build and publish to Pages, but only runs
  when started by hand. Pages on a private repo needs a paid GitHub plan.
