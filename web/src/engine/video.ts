import {
  ALL_FORMATS, BlobSource, BufferTarget, CanvasSource, Conversion, ConversionCanceledError, Input, Mp4OutputFormat, Output,
  Quality, canEncodeAudio, canEncodeVideo,
} from 'mediabunny';
import { decide, fmtMb, planVideo, type VideoInfo, type VideoPlan } from '../core/planner';
import { Tuning } from '../core/tuning';
import { SquishError, cancelled, outputName, throwIfAborted, type JobRequest, type JobResult } from './types';

export type ProbedVideo = {
  info: VideoInfo;
  container: string;
  videoCodec: string | null;
  canDecode: boolean;
  hdr: boolean;
};

const openInput = (file: Blob) => new Input({ formats: ALL_FORMATS, source: new BlobSource(file) });

export async function probeVideo(file: File): Promise<ProbedVideo> {
  const input = openInput(file);
  try {
    let video, audio;
    try {
      video = await input.getPrimaryVideoTrack();
      audio = await input.getPrimaryAudioTrack();
    } catch (e) {
      throw new SquishError('unsupported', "This isn't a video or image Squish can read. It may be damaged or in an unsupported format.", e);
    }
    if (!video && audio) throw new SquishError('audio-only', 'This is an audio file. Squish only compresses videos and images.');
    if (!video) throw new SquishError('unsupported', 'No video found in this file.');

    const duration = await input.computeDuration();
    if (!(duration > 0)) throw new SquishError('no-duration', "Couldn't work out how long this video is. It may be damaged.");

    const [width, height, stats, videoCodec, canDecode, hdr] = await Promise.all([
      video.getDisplayWidth(), video.getDisplayHeight(), video.computePacketStats(120), video.getCodec(),
      video.canDecode(), video.hasHighDynamicRange().catch(() => false),
    ]);
    let audioCodec: string | null = null, audioChannels = 0, audioBitrate = 0;
    if (audio) {
      [audioCodec, audioChannels] = await Promise.all([audio.getCodec(), audio.getNumberOfChannels()]);
      audioBitrate = (await audio.computePacketStats(200)).averageBitrate || 0;
    }
    // Whole-file average is more reliable than a packet sample for the "never inflate" cap.
    const overall = (file.size * 8) / duration;
    const videoBitrate = Math.max(0, Math.min(stats.averageBitrate || overall, overall) - audioBitrate);
    return {
      info: {
        width, height, duration, fps: stats.averagePacketRate || 30, videoBitrate,
        hasAudio: !!audio, audioCodec, audioBitrate, audioChannels,
      },
      container: (await input.getFormat()).name,
      videoCodec,
      canDecode,
      hdr,
    };
  } finally {
    input.dispose();
  }
}

let aacReady: Promise<void> | null = null;
/** Firefox and some Android builds have no AAC encoder; load the WASM one only when needed. */
function ensureAac(): Promise<void> {
  return (aacReady ??= (async () => {
    if (await canEncodeAudio('aac')) return;
    const { registerAacEncoder } = await import('@mediabunny/aac-encoder');
    registerAacEncoder();
  })());
}

/** Largest planned size the device's H.264 encoder accepts; phones may not encode 4K. */
async function fitToEncoder(info: VideoInfo, plan: VideoPlan): Promise<VideoPlan> {
  let p = plan;
  for (let i = 0; i < 6; i++) {
    if (await canEncodeVideo('avc', { width: p.width, height: p.height, bitrate: p.videoKbps * 1000 })) return p;
    const scale = 0.75;
    p = { ...p, width: Math.max(2, Math.round((p.width * scale) / 2) * 2), height: Math.max(2, Math.round((p.height * scale) / 2) * 2) };
    p.shortSide = Math.min(p.width, p.height);
  }
  throw new SquishError('browser', "This browser can't encode H.264 video. Try Chrome, or Safari on iOS 17 or later.", info);
}

async function runConversion(
  file: File, plan: VideoPlan, signal: AbortSignal, onProgress: (f: number) => void,
): Promise<Blob> {
  const input = openInput(file);
  const output = new Output({ format: new Mp4OutputFormat({ fastStart: 'in-memory' }), target: new BufferTarget() });
  try {
    const conversion = await Conversion.init({
      input, output, tracks: 'primary', showWarnings: false,
      tags: {}, // drop metadata: phone videos carry GPS location
      video: {
        width: plan.width, height: plan.height, fit: 'contain', frameRate: plan.fps, codec: 'avc',
        quality: new Quality({ bitrate: plan.videoKbps * 1000, bitrateMode: 'variable' }),
        forceTranscode: true, allowTransformationMetadata: false, alpha: 'discard',
      },
      audio: !plan.includeAudio ? { discard: true }
        : plan.copyAudio ? {}
        : { codec: 'aac', quality: new Quality({ bitrate: plan.audioKbps * 1000 }), numberOfChannels: plan.audioChannels, forceTranscode: true },
    });
    if (!conversion.isValid) {
      throw new SquishError('browser', "This browser can't convert this video. Try Chrome, or Safari on iOS 17 or later.", conversion.discardedTracks);
    }
    conversion.onProgress = (p) => onProgress(p);
    const abort = () => void conversion.cancel();
    // A cancel can land while init() was still reading the file; honour it before starting.
    if (signal.aborted) { await conversion.cancel(); throw cancelled(); }
    signal.addEventListener('abort', abort, { once: true });
    try {
      await conversion.execute();
    } finally {
      signal.removeEventListener('abort', abort);
    }
    const buffer = (output.target as BufferTarget).buffer;
    if (!buffer) throw new SquishError('encoder', 'The video encoder produced no output.');
    return new Blob([buffer], { type: 'video/mp4' });
  } catch (e) {
    if (e instanceof ConversionCanceledError || signal.aborted) throw cancelled();
    if (e instanceof SquishError) throw e;
    throw new SquishError('encoder', 'The video encoder stopped with an error. Try Best quality on the desktop app, or a smaller size.', e);
  } finally {
    input.dispose();
  }
}

/** Already fits but isn't MP4 (e.g. MOV from an iPhone): copy streams into MP4 without re-encoding. */
async function remux(file: File, signal: AbortSignal): Promise<Blob | null> {
  const input = openInput(file);
  const output = new Output({ format: new Mp4OutputFormat({ fastStart: 'in-memory' }), target: new BufferTarget() });
  try {
    const conversion = await Conversion.init({ input, output, tracks: 'primary', showWarnings: false, tags: {}, copy: { mode: 'forced' } });
    if (!conversion.isValid || conversion.discardedTracks.length > 0) return null;
    if (signal.aborted) { await conversion.cancel(); throw cancelled(); }
    signal.addEventListener('abort', () => void conversion.cancel(), { once: true });
    await conversion.execute();
    const buffer = (output.target as BufferTarget).buffer;
    return buffer ? new Blob([buffer], { type: 'video/mp4' }) : null;
  } catch {
    if (signal.aborted) throw cancelled();
    return null;
  } finally {
    input.dispose();
  }
}

export async function compressVideo(req: JobRequest, probed: ProbedVideo): Promise<JobResult> {
  const { file, targetBytes, signal } = req;
  const { info } = probed;
  if (!probed.canDecode)
    throw new SquishError('browser', `This browser can't play ${probed.videoCodec?.toUpperCase() ?? 'this'} video, so it can't compress it. Try Chrome or Safari, or the desktop app.`);

  // Step 1: skip or pass through.
  const friendlyVideo = probed.videoCodec === 'avc' || probed.videoCodec === 'hevc';
  const friendlyAudio = !info.hasAudio || info.audioCodec === 'aac';
  if (file.size <= targetBytes * Tuning.missFraction && friendlyVideo && friendlyAudio) {
    if (probed.container === 'MP4') return { blob: file, name: outputName(file.name, '.mp4'), alreadyFit: true, summary: 'No compression needed', warnings: [] };
    req.onProgress(0, 'Repackaging as MP4');
    const blob = await remux(file, signal);
    if (blob && blob.size <= targetBytes) return { blob, name: outputName(file.name, '.mp4'), alreadyFit: true, summary: 'No compression needed', warnings: [] };
  }

  await ensureAac();
  let plan = await fitToEncoder(info, planVideo(info, targetBytes));
  const warnings = [...plan.warnings];
  if (probed.hdr) warnings.push('HDR video: colours may look flatter after converting.');
  let fallback: { blob: Blob; plan: VideoPlan } | null = null;

  for (;;) {
    throwIfAborted(signal);
    const label = plan.attempt === 1 ? 'Encoding' : `Encoding · attempt ${plan.attempt}`;
    req.onProgress(0, label);
    const blob = await runConversion(file, plan, signal, (f) => req.onProgress(f, label));
    const verdict = decide(info, plan, targetBytes, blob.size);
    if (verdict.kind === 'pass') return done(blob, plan);
    if (verdict.kind === 'retry') {
      if (!verdict.fits && fallback) return done(fallback.blob, fallback.plan); // a raise overshot: keep the earlier fit
      if (verdict.fits) fallback = { blob, plan };
      plan = await fitToEncoder(info, verdict.next);
      continue;
    }
    if (fallback) return done(fallback.blob, fallback.plan);
    throw new SquishError('unreachable', `Couldn't get this under ${fmtMb(targetBytes / 1e6)} MB. Pick a larger size, or trim the clip first.`);
  }

  function done(blob: Blob, p: VideoPlan): JobResult {
    // Final guarantee, measured on the actual bytes.
    if (blob.size > targetBytes) throw new SquishError('unreachable', 'Output came out over the target. Pick a larger size.');
    return {
      blob, name: outputName(file.name, '.mp4'), alreadyFit: false,
      summary: `${p.shortSide}p · ${Math.round(p.fps)} fps`, warnings,
    };
  }
}

/** Encodes canvas frames (from an animated image) to MP4 at a fixed bitrate. Used by "Convert to video". */
export async function encodeFramesToMp4(
  width: number, height: number, fps: number, kbps: number,
  drawFrames: (draw: (timestamp: number, duration: number) => Promise<void>, ctx: OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D) => Promise<void>,
  signal: AbortSignal,
): Promise<Blob> {
  const canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(width, height) : Object.assign(document.createElement('canvas'), { width, height });
  const ctx = canvas.getContext('2d') as OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D;
  const output = new Output({ format: new Mp4OutputFormat({ fastStart: 'in-memory' }), target: new BufferTarget() });
  const source = new CanvasSource(canvas, { codec: 'avc', quality: new Quality({ bitrate: kbps * 1000, bitrateMode: 'variable' }) });
  output.addVideoTrack(source, { frameRate: fps });
  await output.start();
  try {
    await drawFrames(async (t, d) => {
      if (signal.aborted) throw cancelled();
      await source.add(t, d);
    }, ctx);
    source.close();
    await output.finalize();
  } catch (e) {
    await output.cancel().catch(() => {});
    throw e;
  }
  return new Blob([(output.target as BufferTarget).buffer!], { type: 'video/mp4' });
}
