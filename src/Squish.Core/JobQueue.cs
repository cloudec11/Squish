namespace Squish.Core;

/// <summary>
/// Bounded concurrency (plan section 10): one FFmpeg encode at a time (libx264 uses every core),
/// still images in parallel up to half the logical cores, probing in parallel ahead of both.
/// </summary>
public sealed class JobQueue(Compressor compressor, int? imageParallelism = null)
{
    private readonly SemaphoreSlim _ffmpeg = new(1, 1);
    private readonly SemaphoreSlim _images = new(imageParallelism ?? Math.Max(1, Environment.ProcessorCount / 2));
    private readonly SemaphoreSlim _probes = new(Math.Max(2, Environment.ProcessorCount / 2));

    public Compressor Compressor => compressor;

    /// <summary>Probe a file without holding an encode slot.</summary>
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        await _probes.WaitAsync(ct).ConfigureAwait(false);
        try { return await compressor.Probe.ProbeAsync(path, ct).ConfigureAwait(false); }
        finally { _probes.Release(); }
    }

    public async Task<CompressionResult> RunAsync(
        CompressionRequest req, IProgress<JobProgress>? progress, CancellationToken ct, MediaInfo? info = null)
    {
        info ??= await ProbeAsync(req.InputPath, ct).ConfigureAwait(false);
        bool usesFfmpeg = info.Kind != MediaKind.StillImage;
        var gate = usesFfmpeg ? _ffmpeg : _images;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await compressor.RunAsync(req, progress, ct, info).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
