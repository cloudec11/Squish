/**
 * Starting values from the build plan, shared in spirit with src/Squish.Core/Tuning.cs.
 * The browser encodes video single-pass on hardware, like the desktop app's Fast mode, so it aims for 94%.
 */
export const Tuning = {
  aimFraction: 0.94,
  imageAimFraction: 0.97,
  missFraction: 0.995,
  underuseFraction: 0.93,
  containerOverhead: 0.005,
  heightLadder: [2160, 1440, 1080, 720, 540, 480, 360],
  bppFloor: 0.045,
  maxFrameRate: 60,
  shortClipSeconds: 3,
  retryShrink: 0.98,
  maxRaiseFactor: 1.35,
  maxVideoAttempts: 5,
  /** [minimum total kbps, audio kbps, channels]; first match wins. */
  audioTiers: [
    [2000, 128, 2],
    [1000, 96, 2],
    [400, 64, 2],
    [0, 48, 1],
  ] as const,

  imageQualityMax: 95,
  imageQualityMin: 60,
  imageQualityLastResort: 40,
  imageMaxSearchEncodes: 7,
  imageDownscaleStep: 0.85,
  imageLongEdgeFloor: 1280,

  gifMaxFps: 30,
  gifScaleStep: 0.85,
  gifLongEdgeFloor: 320,
  gifFpsSteps: [20, 15],
  gifColourSteps: [256, 128, 64],
  gifMaxAttempts: 12,
} as const;

export type SizePreset = { id: string; label: string; megabytes: number; caption: string };

/** Same presets as src/Squish.Core/presets.json. 1 MB = 1,000,000 bytes until Discord's real ceiling is measured. */
export const Presets: SizePreset[] = [
  { id: 'safe', label: '10', megabytes: 10, caption: 'Safe for older clients' },
  { id: 'free', label: '20', megabytes: 20, caption: 'Free limit' },
  { id: 'basic', label: '50', megabytes: 50, caption: 'Nitro Basic' },
  { id: 'boosted', label: '100', megabytes: 100, caption: 'Level 3 boosted server' },
  { id: 'nitro', label: '500', megabytes: 500, caption: 'Nitro' },
];

export const BYTES_PER_MB = 1_000_000;
export const DEFAULT_MB = 20;
export const CUSTOM_MIN_MB = 1;
export const CUSTOM_MAX_MB = 2000;

export const toBytes = (mb: number) => Math.floor(mb * BYTES_PER_MB);
