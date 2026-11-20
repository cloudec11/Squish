namespace Squish.Core;

/// <summary>Best = two-pass libx264. Fast = hardware H.264 when available.</summary>
public enum QualityMode { Best, Fast }

public enum CodecPreference { H264, Hevc, Av1 }

/// <summary>
/// Output format override. Auto follows the tables in plan sections 6 and 7.
/// Video means "convert an animated image to MP4".
/// </summary>
public enum OutputFormatPreference { Auto, Jpeg, Png, WebP, Gif, Video }

/// <summary>
/// One file to compress. OutputDirectory null means "beside the source".
/// The final file name is decided when the job finishes, because the extension depends on the plan.
/// </summary>
public sealed record CompressionRequest(
    string InputPath,
    string? OutputDirectory,
    long TargetBytes,
    QualityMode Mode = QualityMode.Best,
    CodecPreference Codec = CodecPreference.H264,
    OutputFormatPreference Format = OutputFormatPreference.Auto);

public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct);
}

public interface IPlanner
{
    EncodePlan Plan(MediaInfo info, CompressionRequest req, Capabilities caps);
}

public interface IEncoder
{
    Task<EncodeResult> RunAsync(EncodePlan plan, IProgress<JobProgress> p, CancellationToken ct);
}

/// <summary>
/// Async because the playability check runs ffprobe on the output.
/// Returns Pass, Fail, or Retry with a corrected plan.
/// </summary>
public interface IVerifier
{
    Task<VerifyResult> CheckAsync(EncodeResult result, EncodePlan plan, CancellationToken ct);
}

/// <summary>Progress for one job. Percent is 0..100 across the whole job, Step is a short label.</summary>
public sealed record JobProgress(double Percent, string Step, TimeSpan? Remaining = null);

public enum JobOutcome
{
    /// <summary>Compressed and fits.</summary>
    Compressed,
    /// <summary>Source already fit; copied or remuxed without re-encoding.</summary>
    AlreadyFits,
}

public sealed record CompressionResult(
    string InputPath,
    string OutputPath,
    long InputBytes,
    long OutputBytes,
    long TargetBytes,
    JobOutcome Outcome,
    int Retries,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings,
    string Summary);
