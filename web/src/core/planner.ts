import { Tuning } from './tuning';

/** What the planner needs to know about a video. Dimensions are as displayed (rotation applied). */
export type VideoInfo = {
  width: number;
  height: number;
  duration: number;
  fps: number;
  videoBitrate: number; // bits per second, 0 if unknown
  hasAudio: boolean;
  audioCodec: string | null;
  audioBitrate: number; // bits per second, 0 if unknown
  audioChannels: number;
};

export type VideoPlan = {
  width: number;
  height: number;
  fps: number;
  videoKbps: number;
  audioKbps: number;
  audioChannels: number;
  includeAudio: boolean;
  copyAudio: boolean;
  shortSide: number;
  rung: number;
  bpp: number;
  tooLong: boolean;
  cappedAtSource: boolean;
  predictedBytes: number;
  warnings: string[];
  // Retry state
  attempt: number;
  raised: boolean;
};

const MIN_VIDEO_KBPS = 30;

export function audioFor(totalKbps: number): { kbps: number; channels: number } {
  for (const [min, kbps, channels] of Tuning.audioTiers) {
    if (totalKbps > min || min === 0) return { kbps, channels };
  }
  return { kbps: 48, channels: 1 };
}

const even = (v: number) => Math.max(2, Math.round(v / 2) * 2);
const shortSideOf = (i: { width: number; height: number }) => Math.min(i.width, i.height);

/** Short sides to try, largest first. Never upscales; a source between rungs is tried at its own size first. */
export function rungsFor(info: VideoInfo): number[] {
  const src = shortSideOf(info);
  const rungs: number[] = Tuning.heightLadder.filter((h) => h <= src);
  if (rungs.length === 0 || rungs[0] !== src) rungs.unshift(src);
  return rungs;
}

/** Output dimensions for a short side, keeping aspect ratio, even numbers. */
export function dimsFor(info: { width: number; height: number }, shortSide: number): [number, number] {
  const { width: w, height: h } = info;
  if (shortSide >= Math.min(w, h)) return [even(w), even(h)];
  return w >= h ? [even((shortSide * w) / h), even(shortSide)] : [even(shortSide), even((shortSide * h) / w)];
}

type RungChoice = { rung: number; side: number; width: number; height: number; fps: number; bpp: number; tooLong: boolean };

export function chooseRung(info: VideoInfo, videoKbps: number): RungChoice {
  const fps = Math.min(info.fps > 0 ? info.fps : 30, Tuning.maxFrameRate);
  const half = fps > 30 ? fps / 2 : fps;
  const rungs = rungsFor(info);
  const high = rungs.filter((r) => r >= 720);
  const low = rungs.filter((r) => r < 720);

  const candidates: [number, number][] = [];
  for (const r of high) candidates.push([r, fps]);
  if (fps > 30) for (const r of high) candidates.push([r, half]);
  if (high.length === 0) candidates.push([low[0], fps]); // sub-720 sources keep their fps when there's room
  for (const r of low) candidates.push([r, half]);

  for (const [side, f] of candidates) {
    const [w, h] = dimsFor(info, side);
    const bpp = (videoKbps * 1000) / (w * h * f);
    if (bpp >= Tuning.bppFloor) return { rung: rungs.indexOf(side), side, width: w, height: h, fps: f, bpp, tooLong: false };
  }
  const last = rungs[rungs.length - 1];
  const lastFps = Math.min(half, 24);
  const [lw, lh] = dimsFor(info, last);
  const lbpp = (videoKbps * 1000) / (lw * lh * lastFps);
  return { rung: rungs.length - 1, side: last, width: lw, height: lh, fps: lastFps, bpp: lbpp, tooLong: lbpp < Tuning.bppFloor };
}

/** Plan section 6, steps 2-4: bitrate budget, audio tier, resolution and frame rate. Pure. */
export function planVideo(info: VideoInfo, targetBytes: number): VideoPlan {
  const seconds = Math.max(info.duration, 0.1);
  const budget = targetBytes * Tuning.aimFraction;
  const totalKbps = (8 * budget) / seconds / 1000;

  let audioKbps = 0;
  let channels = 0;
  let copyAudio = false;
  if (info.hasAudio) {
    ({ kbps: audioKbps, channels } = audioFor(totalKbps));
    const srcKbps = Math.floor(info.audioBitrate / 1000);
    if (info.audioCodec === 'aac' && srcKbps > 0 && srcKbps <= audioKbps && info.audioChannels >= 1 && info.audioChannels <= 2) {
      copyAudio = true;
      audioKbps = srcKbps;
      channels = info.audioChannels;
    }
  }

  let videoKbps = totalKbps * (1 - Tuning.containerOverhead) - audioKbps;
  let capped = false;
  const srcVideoKbps = info.videoBitrate / 1000;
  if (srcVideoKbps > 0) {
    const cap = info.duration < Tuning.shortClipSeconds ? srcVideoKbps : srcVideoKbps * 1.5;
    if (videoKbps > cap) {
      videoKbps = cap;
      capped = true;
    }
  }
  videoKbps = Math.max(MIN_VIDEO_KBPS, videoKbps);

  const c = chooseRung(info, videoKbps);
  const mb = targetBytes / 1_000_000;
  const warnings: string[] = [];
  if (c.tooLong) warnings.push(`Too long for ${fmtMb(mb)} MB: it will look very soft. Try a larger size.`);
  else if (c.side < 480 && shortSideOf(info) >= 480) warnings.push(`Long clip: will look soft at ${fmtMb(mb)} MB`);

  const predicted = Math.min(((videoKbps + audioKbps) * 1000) / 8 * seconds * (1 + Tuning.containerOverhead), budget);
  return {
    width: c.width,
    height: c.height,
    fps: c.fps,
    videoKbps: Math.round(videoKbps),
    audioKbps,
    audioChannels: channels,
    includeAudio: info.hasAudio,
    copyAudio,
    shortSide: c.side,
    rung: c.rung,
    bpp: c.bpp,
    tooLong: c.tooLong,
    cappedAtSource: capped,
    predictedBytes: Math.round(predicted),
    warnings,
    attempt: 1,
    raised: false,
  };
}

export type Verdict =
  | { kind: 'pass' }
  | { kind: 'retry'; next: VideoPlan; fits: boolean }
  | { kind: 'fail' };

/**
 * Plan section 6, step 7, adapted to single-pass browser encoders: every retry is a full encode, so a miss
 * shrinks the bitrate and, from the second miss on, also drops a rung (halving fps first above 30 fps).
 */
export function decide(info: VideoInfo, plan: VideoPlan, targetBytes: number, actual: number): Verdict {
  const fits = actual <= targetBytes * Tuning.missFraction;
  if (!fits) {
    if (plan.attempt >= Tuning.maxVideoAttempts) return { kind: 'fail' };
    const kbps = Math.max(20, Math.floor(plan.videoKbps * (targetBytes / actual) * Tuning.retryShrink));
    let next: VideoPlan = { ...plan, videoKbps: kbps, attempt: plan.attempt + 1 };
    if (plan.attempt >= 2) next = dropRung(info, next);
    return { kind: 'retry', next, fits: false };
  }
  if (actual < targetBytes * Tuning.underuseFraction && !plan.cappedAtSource && !plan.raised && plan.attempt < Tuning.maxVideoAttempts) {
    const factor = Math.min(Tuning.maxRaiseFactor, (targetBytes * Tuning.aimFraction) / actual);
    if (factor > 1.01)
      return { kind: 'retry', fits: true, next: { ...plan, videoKbps: Math.floor(plan.videoKbps * factor), raised: true, attempt: plan.attempt + 1 } };
  }
  return { kind: 'pass' };
}

function dropRung(info: VideoInfo, p: VideoPlan): VideoPlan {
  const rungs = rungsFor(info);
  const current = Math.min(Math.max(p.rung, 0), rungs.length - 1);
  const nextRung = current + 1;
  const below720 = nextRung < rungs.length && rungs[nextRung] < 720;
  if (p.fps > 30 && below720 && rungs[current] >= 720) return { ...p, fps: p.fps / 2 };
  if (nextRung >= rungs.length) return p;
  const [w, h] = dimsFor(info, rungs[nextRung]);
  return { ...p, width: w, height: h, rung: nextRung, shortSide: rungs[nextRung], fps: below720 && p.fps > 30 ? p.fps / 2 : p.fps };
}

export const fmtMb = (mb: number) => (Math.round(mb * 10) / 10).toString();

// ---------------- Images and GIFs

/** Largest step^k that is at most s. */
export function snapScale(s: number, step: number): number {
  if (s >= 1) return 1;
  return Math.pow(step, Math.ceil(Math.log(s) / Math.log(step)));
}

/** Scale keeping aspect, never below the long-edge floor (unless the source is already smaller). */
export function scaleDims(w: number, h: number, scale: number, longEdgeFloor: number): [number, number] {
  const minScale = Math.min(1, longEdgeFloor / Math.max(w, h));
  const s = Math.min(1, Math.max(minScale, scale));
  return [Math.max(2, Math.round((w * s) / 2) * 2), Math.max(2, Math.round((h * s) / 2) * 2)];
}

/** Binary search for the highest quality in [lo, hi] for which `fits` returns a result, at most `maxTries` probes. */
export async function searchQuality<T>(
  lo: number,
  hi: number,
  maxTries: number,
  fits: (q: number) => Promise<T | null>,
): Promise<{ result: T; quality: number } | null> {
  let best: { result: T; quality: number } | null = null;
  let tries = 0;
  while (lo <= hi && tries < maxTries) {
    const mid = Math.floor((lo + hi + 1) / 2);
    tries++;
    const r = await fits(mid);
    if (r !== null) {
      best = { result: r, quality: mid };
      lo = mid + 1;
    } else hi = mid - 1;
  }
  return best;
}
