/** Identifies a file by its first bytes, never by its name. Same rules as the desktop ContentSniffer. */
export type Sniffed =
  | { kind: 'image'; format: ImageFormat; animated: boolean; alpha: boolean }
  | { kind: 'other' }; // video or unknown: let the media demuxer decide

export type ImageFormat = 'jpeg' | 'png' | 'gif' | 'webp' | 'heif' | 'avif' | 'bmp' | 'tiff';

const ascii = (b: Uint8Array, start: number, end: number) => String.fromCharCode(...b.subarray(start, end));

export function sniffBytes(b: Uint8Array): Sniffed {
  if (b.length < 12) return { kind: 'other' };
  if (b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return img('jpeg');
  if (b[0] === 0x89 && ascii(b, 1, 4) === 'PNG') return sniffPng(b);
  if (ascii(b, 0, 6) === 'GIF87a' || ascii(b, 0, 6) === 'GIF89a') return img('gif', false, true);
  if (ascii(b, 0, 4) === 'RIFF' && ascii(b, 8, 12) === 'WEBP') return sniffWebp(b);
  if (ascii(b, 0, 2) === 'BM') return img('bmp');
  if (ascii(b, 0, 4) === 'II*\0' || ascii(b, 0, 4) === 'MM\0*') return img('tiff');
  if (ascii(b, 4, 8) === 'ftyp') {
    const brand = ascii(b, 8, 12);
    if (['heic', 'heix', 'hevc', 'hevx', 'heim', 'heis', 'mif1', 'msf1'].includes(brand)) return img('heif');
    if (brand === 'avif' || brand === 'avis') return img('avif', brand === 'avis');
  }
  return { kind: 'other' };
}

function img(format: ImageFormat, animated = false, alpha = false): Sniffed {
  return { kind: 'image', format, animated, alpha };
}

function sniffWebp(b: Uint8Array): Sniffed {
  const chunk = ascii(b, 12, 16);
  if (chunk === 'VP8X' && b.length > 20) return img('webp', (b[20] & 0x02) !== 0, (b[20] & 0x10) !== 0);
  if (chunk === 'VP8L' && b.length > 24) {
    const bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
    return img('webp', false, ((bits >>> 28) & 1) !== 0);
  }
  return img('webp');
}

/** Reads IHDR colour type and looks for acTL (APNG) / tRNS before the first IDAT. Needs the first few KB. */
function sniffPng(b: Uint8Array): Sniffed {
  let alpha = false;
  let animated = false;
  const view = new DataView(b.buffer, b.byteOffset, b.byteLength);
  let pos = 8;
  for (let i = 0; i < 64 && pos + 8 <= b.length; i++) {
    const len = view.getUint32(pos);
    const type = ascii(b, pos + 4, pos + 8);
    if (type === 'IHDR' && pos + 17 < b.length) alpha = b[pos + 17] === 4 || b[pos + 17] === 6;
    if (type === 'acTL') animated = true;
    if (type === 'tRNS') alpha = true;
    if (type === 'IDAT') break;
    pos += 12 + len;
  }
  return img('png', animated, alpha);
}

export async function sniffFile(file: Blob): Promise<Sniffed> {
  return sniffBytes(new Uint8Array(await file.slice(0, 4096).arrayBuffer()));
}

/** Formats Discord previews inline, so a file that already fits can be shared untouched. */
export const PREVIEWABLE: ReadonlySet<ImageFormat> = new Set(['jpeg', 'png', 'webp', 'gif']);
