export function mb(bytes: number): string {
  if (bytes >= 100_000_000) return `${Math.round(bytes / 1e6)} MB`;
  if (bytes >= 1_000_000) return `${(bytes / 1e6).toFixed(1)} MB`;
  if (bytes >= 1000) return `${Math.round(bytes / 1e3)} KB`;
  return `${bytes} bytes`;
}