import { searchQuality } from '../core/planner';
import { PREVIEWABLE, type ImageFormat } from '../core/sniff';
import { Tuning } from '../core/tuning';
import { SquishError, cancelled, outputName, throwIfAborted, yieldToUi, type JobRequest, type JobResult } from './types';

type OutFormat = 'jpeg' | 'png' | 'webp';
const MIME: Record<OutFormat, string> = { jpeg: 'image/jpeg', png: 'image/png', webp: 'image/webp' };
const EXT: Record<OutFormat, string> = { jpeg: '.jpg', png: '.png', webp: '.webp' };

/** iOS Safari refuses canvases over ~16.7 megapixels; elsewhere keep a sane ceiling for phone memory. */
const isAppleMobile = () => /iP(hone|ad|od)/.test(navigator.userAgent) || (navigator.userAgent.includes('Mac') && navigator.maxTouchPoints > 1);
const maxCanvasPixels = () => (isAppleMobile() ? 16_777_216 : 50_000_000);

let webpSupport: Promise<boolean> | null = null;
/** Safari's canvas can't write WebP (it silently returns PNG), so check once by encoding a pixel. */
export function canEncodeWebp(): Promise<boolean> {
  return (webpSupport ??= (async () => {
    try {
      const c = makeCanvas(1, 1);
      ctx2d(c).fillRect(0, 0, 1, 1); // an OffscreenCanvas can't be encoded until it has a context
      const b = await toBlob(c, 'image/webp', 0.8);
      return b.type === 'image/webp';
    } catch {
      return false;
    }
  })());
}

type AnyCanvas = HTMLCanvasElement | OffscreenCanvas;
function makeCanvas(w: number, h: number): AnyCanvas {
  if (typeof OffscreenCanvas !== 'undefined') return new OffscreenCanvas(w, h);
  return Object.assign(document.createElement('canvas'), { width: w, height: h });
}
function ctx2d(c: AnyCanvas) {
  const ctx = c.getContext('2d', { alpha: true }) as CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D | null;
  if (!ctx) throw new SquishError('browser', "This browser couldn't create an image canvas.");
  return ctx;
}
function toBlob(c: AnyCanvas, type: string, quality?: number): Promise<Blob> {
  if ('convertToBlob' in c) return c.convertToBlob({ type, quality });
  return new Promise((res, rej) => c.toBlob((b) => (b ? res(b) : rej(new Error('encode failed'))), type, quality));
}

/** Decodes with EXIF orientation applied (the browser does this), at a size the device can hold. */
async function decode(file: File): Promise<ImageBitmap> {
  let bmp: ImageBitmap;
  try {
    bmp = await createImageBitmap(file, { imageOrientation: 'from-image' });
  } catch {
    // Some browsers only decode HEIC/AVIF through <img>.
    const url = URL.createObjectURL(file);
    try {
      const img = new Image();
      img.src = url;
      await img.decode();
      bmp = await createImageBitmap(img);
    } catch (e) {
      throw new SquishError('browser', "This browser can't open this image type. Try Chrome or Safari, or the desktop app.", e);
    } finally {
      URL.revokeObjectURL(url);
    }
  }
  const px = bmp.width * bmp.height;
  const limit = maxCanvasPixels();
  if (px > limit) {
    const s = Math.sqrt(limit / px) * 0.99;
    const smaller = await createImageBitmap(bmp, { resizeWidth: Math.floor(bmp.width * s), resizeHeight: Math.floor(bmp.height * s), resizeQuality: 'high' });
    bmp.close();
    bmp = smaller;
  }
  return bmp;
}

function hasTransparency(bmp: ImageBitmap): boolean {
  const c = makeCanvas(64, 64);
  const ctx = ctx2d(c);
  ctx.drawImage(bmp, 0, 0, 64, 64);
  const data = ctx.getImageData(0, 0, 64, 64).data;
  for (let i = 3; i < data.length; i += 4) if (data[i] < 250) return true;
  return false;
}

/** Draws at the requested long edge. Halving in steps keeps big downscales sharp (a single bilinear step aliases). */
function render(bmp: ImageBitmap, longEdge: number, flattenWhite: boolean): AnyCanvas {
  const scale = Math.min(1, longEdge / Math.max(bmp.width, bmp.height));
  const w = Math.max(1, Math.round(bmp.width * scale));
  const h = Math.max(1, Math.round(bmp.height * scale));
  let src: CanvasImageSource = bmp;
  let sw = bmp.width, sh = bmp.height;
  while (sw / 2 >= w && sh / 2 >= h) {
    const half = makeCanvas(Math.round(sw / 2), Math.round(sh / 2));
    const hc = ctx2d(half);
    hc.imageSmoothingQuality = 'high';
    hc.drawImage(src, 0, 0, half.width, half.height);
    src = half; sw = half.width; sh = half.height;
  }
  const out = makeCanvas(w, h);
  const oc = ctx2d(out);
  if (flattenWhite) { oc.fillStyle = '#fff'; oc.fillRect(0, 0, w, h); }
  oc.imageSmoothingQuality = 'high';
  oc.drawImage(src, 0, 0, w, h);
  return out;
}

export async function compressImage(req: JobRequest, format: ImageFormat): Promise<JobResult> {
  const { file, targetBytes, signal } = req;
  const pref = req.format;

  // Skip: fits already and Discord previews it.
  if (file.size <= targetBytes * Tuning.missFraction && PREVIEWABLE.has(format) && (pref === 'auto' || pref === format))
    return { blob: file, name: outputName(file.name, format === 'gif' ? '.gif' : EXT[format as OutFormat]), alreadyFit: true, summary: 'No compression needed', warnings: [] };

  req.onProgress(0.02, 'Reading image');
  const bmp = await decode(file);
  try {
    throwIfAborted(signal);
    const alpha = hasTransparency(bmp);
    const webp = await canEncodeWebp();
    const fitBytes = targetBytes * Tuning.imageAimFraction;
    const warnings: string[] = [];

    // Output format (plan section 7), adjusted for what this browser can encode.
    let lossy: OutFormat;
    let tryLossless = false;
    if (pref === 'jpeg' || pref === 'png' || pref === 'webp') lossy = pref;
    else if (format === 'jpeg') lossy = 'jpeg';
    else if (format === 'png' || format === 'gif') { tryLossless = true; lossy = 'webp'; }
    else if (format === 'webp') lossy = 'webp';
    else lossy = alpha ? 'webp' : 'jpeg';
    if (lossy === 'webp' && !webp) lossy = alpha ? 'png' : 'jpeg';
    if (lossy === 'png') tryLossless = true;
    if (['heif', 'avif', 'bmp', 'tiff'].includes(format) && lossy === 'jpeg') warnings.push('Saved as JPEG so it previews in Discord.');

    const srcLong = Math.max(bmp.width, bmp.height);
    const encode = async (f: OutFormat, quality: number, edge: number): Promise<Blob | null> => {
      throwIfAborted(signal);
      await yieldToUi();
      const canvas = render(bmp, edge, f === 'jpeg' && alpha);
      const blob = await toBlob(canvas, MIME[f], f === 'png' ? undefined : quality / 100);
      return blob.size <= fitBytes ? blob : null;
    };

    const finish = (blob: Blob, f: OutFormat, how: string): JobResult => {
      if (blob.size > targetBytes) throw new SquishError('unreachable', 'Output came out over the target. Pick a larger size.');
      return { blob, name: outputName(file.name, EXT[f]), alreadyFit: false, summary: `${f.toUpperCase()}, ${how}`, warnings };
    };

    // 1. Lossless.
    if (tryLossless) {
      req.onProgress(0.1, 'Trying lossless');
      const b = await encode('png', 100, srcLong);
      if (b) return finish(b, 'png', 'lossless');
    }

    // PNG only (forced, or no WebP and transparency): shrink the size until it fits.
    if (lossy === 'png') {
      let edge = srcLong;
      while (edge > 16) {
        edge = Math.floor(edge * Tuning.imageDownscaleStep);
        req.onProgress(0.3, `Resizing to ${edge} px`);
        const b = await encode('png', 100, edge);
        if (b) return finish(b, 'png', `lossless, resized to ${edge} px`);
      }
      throw unreachable();
    }

    // 2-5. Quality search, downscaling between rounds, quality floor 40 at or below 1280 px.
    let edge = srcLong;
    for (let round = 0; edge > 16; round++) {
      req.onProgress(Math.min(0.9, 0.2 + round * 0.12), round === 0 ? 'Finding best quality' : `Resizing to ${edge} px`);
      let hit = await searchQuality(Tuning.imageQualityMin, Tuning.imageQualityMax, Tuning.imageMaxSearchEncodes, (q) => encode(lossy, q, edge));
      if (!hit && edge <= Tuning.imageLongEdgeFloor)
        hit = await searchQuality(Tuning.imageQualityLastResort, Tuning.imageQualityMin - 1, Tuning.imageMaxSearchEncodes, (q) => encode(lossy, q, edge));
      if (hit) {
        if (hit.quality < Tuning.imageQualityMin || edge < Tuning.imageLongEdgeFloor) warnings.push('Reduced heavily to fit. Try a larger size for a sharper image.');
        return finish(hit.result, lossy, edge < srcLong ? `quality ${hit.quality}, resized to ${edge} px` : `quality ${hit.quality}`);
      }
      let next = Math.floor(edge * Tuning.imageDownscaleStep);
      if (edge > Tuning.imageLongEdgeFloor && next < Tuning.imageLongEdgeFloor) next = Tuning.imageLongEdgeFloor;
      edge = next;
    }
    throw unreachable();
  } catch (e) {
    if (signal.aborted) throw cancelled();
    throw e;
  } finally {
    bmp.close();
  }
}

const unreachable = () => new SquishError('unreachable', "This image can't get small enough for that size. Pick a larger size.");
