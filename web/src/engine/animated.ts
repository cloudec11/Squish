import { GIFEncoder, applyPalette, quantize } from 'gifenc';
import { decide, planVideo, scaleDims, snapScale, type VideoInfo, type VideoPlan } from '../core/planner';
import type { ImageFormat } from '../core/sniff';
import { Tuning } from '../core/tuning';
import { SquishError, cancelled, outputName, throwIfAborted, yieldToUi, type JobRequest, type JobResult } from './types';
import { encodeFramesToMp4 } from './video';

type SourceFrame = { index: number; start: number; duration: number }; // seconds
type Decoded = { decoder: ImageDecoder; frames: SourceFrame[]; width: number; height: number; total: number };

const MIME: Partial<Record<ImageFormat, string>> = { gif: 'image/gif', webp: 'image/webp', png: 'image/apng', avif: 'image/avif' };

export async function canDecodeAnimated(format: ImageFormat): Promise<boolean> {
  const mime = MIME[format];
  if (!mime || typeof ImageDecoder === 'undefined') return false;
  try { return await ImageDecoder.isTypeSupported(mime); } catch { return false; }
}

async function open(file: File, format: ImageFormat, signal: AbortSignal): Promise<Decoded> {
  if (!(await canDecodeAnimated(format)))
    throw new SquishError('browser', "This browser can't read animated images. Try Chrome on Android or desktop, or the desktop app.");
  const decoder = new ImageDecoder({ data: await file.arrayBuffer(), type: MIME[format]! });
  await decoder.tracks.ready;
  await decoder.completed;
  const count = decoder.tracks.selectedTrack?.frameCount ?? 1;
  const frames: SourceFrame[] = [];
  let t = 0, width = 0, height = 0;
  for (let i = 0; i < count; i++) {
    if (signal.aborted) { decoder.close(); throw cancelled(); }
    const { image } = await decoder.decode({ frameIndex: i });
    if (i === 0) { width = image.displayWidth; height = image.displayHeight; }
    // Browsers play delays of 0-10 ms as 100 ms; match them.
    let d = (image.duration ?? 100_000) / 1e6;
    if (d <= 0.011) d = 0.1;
    image.close();
    frames.push({ index: i, start: t, duration: d });
    t += d;
  }
  return { decoder, frames, width, height, total: t };
}

/** Frames to emit at `fps`: the source frames when they're already slower, otherwise one sample per tick. */
function schedule(src: Decoded, fps: number): { index: number; duration: number }[] {
  const srcFps = src.frames.length / src.total;
  if (srcFps <= fps + 0.5) return src.frames.map((f) => ({ index: f.index, duration: f.duration }));
  const out: { index: number; duration: number }[] = [];
  const step = 1 / fps;
  let k = 0;
  for (let t = 0; t < src.total - 1e-6; t += step) {
    while (k < src.frames.length - 1 && src.frames[k].start + src.frames[k].duration <= t) k++;
    if (out.length && out[out.length - 1].index === src.frames[k].index) out[out.length - 1].duration += step;
    else out.push({ index: src.frames[k].index, duration: step });
  }
  return out;
}

function makeCtx(w: number, h: number) {
  const c = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(w, h) : Object.assign(document.createElement('canvas'), { width: w, height: h });
  return c.getContext('2d', { willReadFrequently: true }) as OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D;
}

async function drawFrame(src: Decoded, index: number, ctx: OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D, w: number, h: number) {
  const { image } = await src.decoder.decode({ frameIndex: index });
  ctx.clearRect(0, 0, w, h);
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(image, 0, 0, w, h);
  image.close();
}

/**
 * One GIF encode. Stops early once the output passes `limit` and returns an estimate of the full size instead,
 * so oversized attempts cost a fraction of a full encode.
 */
async function encodeGif(
  src: Decoded, w: number, h: number, fps: number, colours: number, limit: number,
  signal: AbortSignal, onFrame: (f: number) => void,
): Promise<{ bytes: Uint8Array | null; size: number }> {
  const plan = schedule(src, fps);
  const ctx = makeCtx(w, h);

  // One palette for the whole clip, built from evenly spaced sample frames.
  const samples = Math.min(8, plan.length);
  const sampleData = new Uint8ClampedArray(w * h * 4 * samples);
  for (let s = 0; s < samples; s++) {
    await drawFrame(src, plan[Math.floor((s * plan.length) / samples)].index, ctx, w, h);
    sampleData.set(ctx.getImageData(0, 0, w, h).data, s * w * h * 4);
  }
  const palette = quantize(sampleData, colours, { format: 'rgba4444', oneBitAlpha: true });
  const transparentIndex = palette.findIndex((c) => c.length > 3 && c[3] === 0);

  const gif = GIFEncoder();
  for (let i = 0; i < plan.length; i++) {
    if (signal.aborted) throw cancelled();
    await drawFrame(src, plan[i].index, ctx, w, h);
    const index = applyPalette(ctx.getImageData(0, 0, w, h).data, palette, 'rgba4444');
    gif.writeFrame(index, w, h, {
      palette: i === 0 ? palette : undefined,
      delay: Math.max(20, Math.round(plan[i].duration * 1000)),
      repeat: 0,
      transparent: transparentIndex >= 0,
      transparentIndex: Math.max(0, transparentIndex),
      dispose: transparentIndex >= 0 ? 2 : -1,
    });
    onFrame((i + 1) / plan.length);
    const written = gif.bytesView().length;
    if (written > limit) return { bytes: null, size: Math.round((written * plan.length) / (i + 1)) };
    if (i % 4 === 3) await yieldToUi();
  }
  gif.finish();
  const bytes = gif.bytes();
  return { bytes, size: bytes.length };
}

export async function compressAnimated(req: JobRequest, format: ImageFormat): Promise<JobResult> {
  const { file, targetBytes, signal } = req;
  if (req.format !== 'video' && file.size <= targetBytes * Tuning.missFraction && (format === 'gif' || format === 'webp'))
    return { blob: file, name: outputName(file.name, format === 'gif' ? '.gif' : '.webp'), alreadyFit: true, summary: 'No compression needed', warnings: [] };

  req.onProgress(0.01, 'Reading frames');
  const src = await open(file, format, signal);
  try {
    return req.format === 'video' ? await toVideo(req, src) : await toGif(req, src, format, file.size);
  } finally {
    src.decoder.close();
  }
}

async function toGif(req: JobRequest, src: Decoded, format: ImageFormat, srcBytes: number): Promise<JobResult> {
  const { targetBytes, signal, file } = req;
  const fitBytes = targetBytes * Tuning.imageAimFraction;
  const srcFps = src.frames.length / src.total;
  let fps = Math.min(srcFps, Tuning.gifMaxFps);
  let colours = 256;
  const srcLong = Math.max(src.width, src.height);

  // First guess for GIF sources: size scales with pixels × frames.
  let scale = 1;
  if (format === 'gif') {
    const expected = srcBytes * (fps / srcFps);
    if (expected > fitBytes) scale = snapScale(Math.sqrt(fitBytes / expected), Tuning.gifScaleStep);
  }
  let [w, h] = scaleDims(src.width, src.height, scale, Tuning.gifLongEdgeFloor);
  const fpsSteps = Tuning.gifFpsSteps.filter((f) => f < fps);
  const colourSteps = Tuning.gifColourSteps.filter((c) => c < colours);
  let best: { bytes: Uint8Array; w: number; h: number; fps: number; colours: number } | null = null;
  let stepsUp = 0;
  let fitScale = 0; // largest scale known to fit
  let overScale = Infinity; // smallest scale known to overshoot

  for (let attempt = 1; attempt <= Tuning.gifMaxAttempts; attempt++) {
    throwIfAborted(signal);
    const step = attempt === 1 ? 'Encoding GIF' : `Encoding GIF · attempt ${attempt}`;
    const r = await encodeGif(src, w, h, fps, colours, fitBytes, signal, (f) => req.onProgress(f, step));
    const scale = w / src.width;
    if (r.bytes) {
      if (!best || w > best.w) best = { bytes: r.bytes, w, h, fps, colours };
      fitScale = Math.max(fitScale, scale);
      // Far under the target: grow toward it. Size grows a bit faster than pixel count, so aim at 85%.
      if (stepsUp < 4 && r.size < fitBytes * 0.75 && w < src.width) {
        stepsUp++;
        let next = Math.min(1, scale * Math.min(2, Math.sqrt((fitBytes * 0.85) / r.size)));
        if (next >= overScale) next = Math.sqrt(fitScale * overScale); // stay below a size that already overshot
        if (next / scale < 1.05) break;
        [w, h] = scaleDims(src.width, src.height, next, 2);
        continue;
      }
      break;
    }
    if (best) {
      // A step up overshot: bisect between the largest fit and this size, then stop.
      overScale = Math.min(overScale, scale);
      const mid = Math.sqrt(fitScale * overScale);
      if (stepsUp >= 4 || mid / fitScale < 1.05) break;
      stepsUp++;
      [w, h] = scaleDims(src.width, src.height, mid, 2);
      continue;
    }

    const longEdge = Math.max(w, h);
    const floor = Math.min(Tuning.gifLongEdgeFloor, srcLong);
    if (longEdge > floor) {
      const current = longEdge / srcLong;
      const target = Math.min(current * Tuning.gifScaleStep, snapScale(current * Math.sqrt(fitBytes / r.size), Tuning.gifScaleStep));
      [w, h] = scaleDims(src.width, src.height, target, Tuning.gifLongEdgeFloor);
    } else if (fpsSteps.length) fps = fpsSteps.shift()!;
    else if (colourSteps.length) colours = colourSteps.shift()!;
    else if (longEdge > 64) [w, h] = scaleDims(src.width, src.height, (longEdge * Tuning.gifScaleStep) / srcLong, 2);
    else break;
  }
  if (!best) throw new SquishError('unreachable', "This animation can't get small enough as a GIF. Use Convert to video, or pick a larger size.");
  const warnings: string[] = [];
  if (Math.max(best.w, best.h) < Math.min(Tuning.gifLongEdgeFloor, srcLong) || best.colours < 256 || best.fps < Math.min(srcFps, 20))
    warnings.push('Reduced heavily to fit. Convert to video keeps far more quality.');
  return {
    blob: new Blob([best.bytes as BlobPart], { type: 'image/gif' }), name: outputName(file.name, '.gif'), alreadyFit: false,
    summary: `${best.w}×${best.h} · ${Math.round(best.fps)} fps · ${best.colours} colours`, warnings,
  };
}

async function toVideo(req: JobRequest, src: Decoded): Promise<JobResult> {
  const { targetBytes, signal, file } = req;
  const info: VideoInfo = {
    width: src.width, height: src.height, duration: src.total, fps: src.frames.length / src.total,
    videoBitrate: 0, hasAudio: false, audioCodec: null, audioBitrate: 0, audioChannels: 0,
  };
  let plan: VideoPlan = planVideo(info, targetBytes);
  let fallback: { blob: Blob; plan: VideoPlan } | null = null;
  for (;;) {
    const p = plan;
    const frames = schedule(src, p.fps);
    const label = p.attempt === 1 ? 'Encoding video' : `Encoding video · attempt ${p.attempt}`;
    const blob = await encodeFramesToMp4(p.width, p.height, p.fps, p.videoKbps, async (add, ctx) => {
      let t = 0;
      for (let i = 0; i < frames.length; i++) {
        await drawFrame(src, frames[i].index, ctx, p.width, p.height);
        await add(t, frames[i].duration);
        t += frames[i].duration;
        req.onProgress((i + 1) / frames.length, label);
      }
    }, signal);
    const v = decide(info, p, targetBytes, blob.size);
    if (v.kind === 'pass') return result(blob, p);
    if (v.kind === 'retry') {
      if (!v.fits && fallback) return result(fallback.blob, fallback.plan);
      if (v.fits) fallback = { blob, plan: p };
      plan = v.next;
      continue;
    }
    if (fallback) return result(fallback.blob, fallback.plan);
    throw new SquishError('unreachable', "Couldn't get this under the target. Pick a larger size.");
  }

  function result(blob: Blob, p: VideoPlan): JobResult {
    if (blob.size > targetBytes) throw new SquishError('unreachable', 'Output came out over the target. Pick a larger size.');
    return { blob, name: outputName(file.name, '.mp4'), alreadyFit: false, summary: `MP4 · ${p.shortSide}p · ${Math.round(p.fps)} fps`, warnings: ['Shows in Discord with player controls.'] };
  }
}
