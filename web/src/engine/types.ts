export type Kind = 'video' | 'image' | 'animated';

export type FormatPref = 'auto' | 'jpeg' | 'png' | 'webp' | 'video';

export type JobRequest = {
  file: File;
  targetBytes: number;
  /** Image format override, or 'video' to turn an animated image into an MP4. */
  format: FormatPref;
  signal: AbortSignal;
  onProgress: (fraction: number, step: string) => void;
};

export type JobResult = {
  blob: Blob;
  name: string;
  alreadyFit: boolean;
  summary: string;
  warnings: string[];
};

export type ErrorCode = 'empty' | 'unsupported' | 'audio-only' | 'no-duration' | 'browser' | 'unreachable' | 'encoder' | 'cancelled';

/** Every failure the UI shows: plain words, what happened and what to do next. */
export class SquishError extends Error {
  constructor(public code: ErrorCode, message: string, public detail?: unknown) {
    super(message);
  }
}

export const cancelled = () => new SquishError('cancelled', 'Cancelled.');

export function throwIfAborted(signal: AbortSignal) {
  if (signal.aborted) throw cancelled();
}

/** Lets the browser paint between heavy steps so the page stays responsive on a phone. */
export const yieldToUi = () => new Promise<void>((r) => setTimeout(r, 0));

export function outputName(original: string, ext: string): string {
  const dot = original.lastIndexOf('.');
  const stem = dot > 0 ? original.slice(0, dot) : original;
  return `${stem}_discord${ext}`;
}
