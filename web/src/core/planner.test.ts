import { describe, expect, it } from 'vitest';
import { audioFor, decide, dimsFor, planVideo, rungsFor, scaleDims, searchQuality, snapScale, type VideoInfo } from './planner';
import { sniffBytes } from './sniff';
import { Tuning } from './tuning';

const MB = 1_000_000;
const video = (o: Partial<VideoInfo> = {}): VideoInfo => ({
  width: 1920, height: 1080, duration: 60, fps: 30, videoBitrate: 20_000_000,
  hasAudio: true, audioCodec: 'opus', audioBitrate: 160_000, audioChannels: 2, ...o,
});

describe('video planner', () => {
  it('follows the budget formula', () => {
    const p = planVideo(video(), 20 * MB);
    const total = (8 * 20e6 * Tuning.aimFraction) / 60 / 1000;
    expect(p.audioKbps).toBe(128);
    expect(p.videoKbps).toBe(Math.round(total * 0.995 - 128));
  });

  it('audio tier boundaries', () => {
    expect(audioFor(2001)).toEqual({ kbps: 128, channels: 2 });
    expect(audioFor(2000)).toEqual({ kbps: 96, channels: 2 });
    expect(audioFor(1000)).toEqual({ kbps: 64, channels: 2 });
    expect(audioFor(400)).toEqual({ kbps: 48, channels: 1 });
  });

  it('copies AAC at or below the tier, never surround', () => {
    expect(planVideo(video({ audioCodec: 'aac', audioBitrate: 96_000 }), 20 * MB).copyAudio).toBe(true);
    expect(planVideo(video({ audioCodec: 'aac', audioBitrate: 96_000, audioChannels: 6 }), 20 * MB).copyAudio).toBe(false);
  });

  it('drops to 720p for a minute of 1080p at 20 MB', () => {
    const p = planVideo(video(), 20 * MB);
    expect(p.height).toBe(720);
    expect(p.bpp).toBeGreaterThanOrEqual(Tuning.bppFloor);
  });

  it('never upscales and tries an in-between source size first', () => {
    expect(rungsFor(video({ width: 1280, height: 800 }))).toEqual([800, 720, 540, 480, 360]);
    const p = planVideo(video({ width: 1280, height: 800, duration: 5 }), 20 * MB);
    expect([p.width, p.height]).toEqual([1280, 800]);
  });

  it('uses the short side for vertical video', () => {
    const p = planVideo(video({ width: 1080, height: 1920 }), 20 * MB);
    expect(p.width).toBe(720);
    expect(p.height).toBeGreaterThan(p.width);
  });

  it('halves 60 fps before going below 720p', () => {
    const p = planVideo(video({ fps: 60, duration: 90 }), 20 * MB);
    expect(p.height).toBe(720);
    expect(p.fps).toBe(30);
  });

  it('keeps 60 fps for a sub-720p clip with room', () => {
    const p = planVideo(video({ width: 854, height: 480, fps: 60, duration: 10 }), 10 * MB);
    expect(p.fps).toBe(60);
  });

  it('flags clips too long for the size', () => {
    const p = planVideo(video({ fps: 60, duration: 1200 }), 20 * MB);
    expect(p.tooLong).toBe(true);
    expect(p.fps).toBe(24);
    expect(p.warnings[0]).toMatch(/Too long/);
  });

  it('caps very short clips at the source bitrate', () => {
    const p = planVideo(video({ duration: 2, videoBitrate: 3_000_000 }), 20 * MB);
    expect(p.cappedAtSource).toBe(true);
    expect(p.videoKbps).toBe(3000);
  });

  it('keeps aspect ratio with even sizes', () => {
    const [w, h] = dimsFor({ width: 1918, height: 1078 }, 720);
    expect(w % 2).toBe(0);
    expect(h).toBe(720);
  });
});

describe('verify and retry', () => {
  const info = video();
  const plan = planVideo(info, 20 * MB);

  it('passes inside the window', () => expect(decide(info, plan, 20 * MB, 19.2 * MB).kind).toBe('pass'));

  it('shrinks bitrate on a miss, then drops a rung on the second', () => {
    const v1 = decide(info, plan, 20 * MB, 20.6 * MB);
    expect(v1.kind).toBe('retry');
    if (v1.kind !== 'retry') return;
    expect(v1.next.videoKbps).toBeLessThan(plan.videoKbps);
    expect(v1.next.height).toBe(plan.height);
    const v2 = decide(info, v1.next, 20 * MB, 20.3 * MB);
    if (v2.kind !== 'retry') throw new Error('expected retry');
    expect(v2.next.height).toBeLessThan(plan.height);
  });

  it('raises once when under-used', () => {
    const v = decide(info, plan, 20 * MB, 15 * MB);
    if (v.kind !== 'retry') throw new Error('expected retry');
    expect(v.fits).toBe(true);
    expect(v.next.videoKbps).toBeGreaterThan(plan.videoKbps);
    expect(decide(info, v.next, 20 * MB, 15 * MB).kind).toBe('pass');
  });

  it('gives up after the attempt limit', () => {
    expect(decide(info, { ...plan, attempt: Tuning.maxVideoAttempts }, 20 * MB, 21 * MB).kind).toBe('fail');
  });
});

describe('image and GIF helpers', () => {
  it('snaps scale to the 0.85 grid', () => {
    expect(snapScale(1, 0.85)).toBe(1);
    expect(snapScale(0.9, 0.85)).toBeCloseTo(0.85);
  });

  it('respects the long-edge floor', () => expect(scaleDims(1600, 900, 0.01, 320)).toEqual([320, 180]));

  it('binary search finds the highest quality that fits', async () => {
    const r = await searchQuality(60, 95, 7, async (q) => (q <= 83 ? q : null));
    expect(r?.quality).toBe(83);
  });
});

describe('sniffing', () => {
  const bytes = (arr: number[]) => new Uint8Array([...arr, ...new Array(64).fill(0)]);
  it('recognises the PNG signature as raw bytes', () =>
    expect(sniffBytes(bytes([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))).toMatchObject({ kind: 'image', format: 'png' }));
  it('recognises JPEG and HEIC', () => {
    expect(sniffBytes(bytes([0xff, 0xd8, 0xff, 0xe0]))).toMatchObject({ format: 'jpeg' });
    expect(sniffBytes(bytes([0, 0, 0, 0x18, ...'ftypheic'.split('').map((c) => c.charCodeAt(0))]))).toMatchObject({ format: 'heif' });
  });
  it('leaves MP4 to the demuxer', () =>
    expect(sniffBytes(bytes([0, 0, 0, 0x18, ...'ftypisom'.split('').map((c) => c.charCodeAt(0))]))).toEqual({ kind: 'other' }));
});
