import { planVideo } from '../core/planner';
import { sniffFile, type ImageFormat } from '../core/sniff';
import { Tuning } from '../core/tuning';
import { compressAnimated } from './animated';
import { compressImage } from './image';
import { mb } from './format';
import { SquishError, type JobRequest, type JobResult, type Kind } from './types';
import { compressVideo, probeVideo, type ProbedVideo } from './video';

export type Probed =
  | { kind: 'video'; video: ProbedVideo; detail: string }
  | { kind: 'image' | 'animated'; format: ImageFormat; width: number; height: number; detail: string };

/** Identifies a file by content and reads enough to describe it on its card. Cheap: no encoding. */
export async function probe(file: File): Promise<Probed> {
  if (file.size === 0) throw new SquishError('empty', 'The file is empty (0 bytes).');
  const s = await sniffFile(file);
  if (s.kind === 'image') {
    let width = 0, height = 0;
    try {
      const bmp = await createImageBitmap(file);
      width = bmp.width; height = bmp.height;
      bmp.close();
    } catch { /* size unknown until encode; HEIC may only decode later through <img> */ }
    const kind: Kind = s.animated || s.format === 'gif' && (await isMultiFrameGif(file)) ? 'animated' : 'image';
    const dims = width ? `${width}×${height} · ` : '';
    return { kind, format: s.format, width, height, detail: `${kind === 'animated' ? `${s.format.toUpperCase()} · ` : ''}${dims}${mb(file.size)}` };
  }
  const video = await probeVideo(file);
  const { info } = video;
  return { kind: 'video', video, detail: `${Math.min(info.width, info.height)}p · ${duration(info.duration)} · ${mb(file.size)}` };
}

/** GIFs are animated if they have more than one image descriptor; scan for a second 0x2C after the header. */
async function isMultiFrameGif(file: File): Promise<boolean> {
  if (typeof ImageDecoder !== 'undefined') {
    try {
      const d = new ImageDecoder({ data: file.stream(), type: 'image/gif' });
      await d.tracks.ready;
      const n = d.tracks.selectedTrack?.frameCount ?? 1;
      d.close();
      if (n > 1) return true;
      // frameCount can be 1 until the whole stream is read; fall through to the byte scan.
    } catch { /* fall back */ }
  }
  const b = new Uint8Array(await file.slice(0, Math.min(file.size, 4 * 1024 * 1024)).arrayBuffer());
  let frames = 0;
  for (let i = 0; i + 9 < b.length; i++) {
    // Graphic Control Extension marks each frame: 0x21 0xF9 0x04
    if (b[i] === 0x21 && b[i + 1] === 0xf9 && b[i + 2] === 0x04 && ++frames > 1) return true;
  }
  return false;
}

/** The card's predicted result, from the planner, before anything is encoded. */
export function predict(p: Probed, file: File, targetBytes: number): { line: string; warning?: string } {
  if (p.kind === 'video') {
    if (file.size <= targetBytes * Tuning.missFraction && (p.video.videoCodec === 'avc' || p.video.videoCodec === 'hevc')
      && (!p.video.info.hasAudio || p.video.info.audioCodec === 'aac'))
      return { line: '→ No compression needed' };
    const plan = planVideo(p.video.info, targetBytes);
    return { line: `→ ${plan.shortSide}p · about ${mb(plan.predictedBytes)}`, warning: plan.warnings[0] };
  }
  if (file.size <= targetBytes * Tuning.missFraction && ['jpeg', 'png', 'webp', 'gif'].includes(p.format)) return { line: '→ No compression needed' };
  return { line: `→ under ${mb(targetBytes * Tuning.imageAimFraction)}`, warning: p.kind === 'animated' && file.size > targetBytes * 4 ? 'Big GIF: Convert to video keeps far more quality.' : undefined };
}

export async function compress(req: JobRequest, p: Probed): Promise<JobResult> {
  if (p.kind === 'video') return compressVideo(req, p.video);
  if (p.kind === 'animated') return compressAnimated(req, p.format);
  return compressImage(req, p.format);
}



function duration(s: number): string {
  const t = Math.round(s);
  const h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), sec = t % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}` : `${m}:${String(sec).padStart(2, '0')}`;
}
