namespace Squish.Core;

public enum ImageFormat { Jpeg, Png, WebP, Gif }

/// <summary>What the encoder will do. Pure data, produced by a planner with no I/O.</summary>
public abstract record EncodePlan
{
    public required MediaInfo Info { get; init; }
    public required CompressionRequest Request { get; init; }
    /// <summary>Extension of the output file, with the dot.</summary>
    public required string OutputExtension { get; init; }
    /// <summary>The planner's size estimate for the card ("about 19.4 MB").</summary>
    public long PredictedBytes { get; init; }
    /// <summary>Short description of the output, e.g. "720p · 30 fps".</summary>
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Source already fits and is in a Discord-friendly format: copy bytes as they are.</summary>
public sealed record CopyPlan : EncodePlan;

/// <summary>Source already fits but the container should change (MKV to MP4). Streams are copied.</summary>
public sealed record RemuxPlan : EncodePlan
{
    public bool IncludeAudio { get; init; }
}

public sealed record VideoPlan : EncodePlan
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double Fps { get; init; }
    public required int VideoKbps { get; init; }
    public int AudioKbps { get; init; }
    public int AudioChannels { get; init; }
    public bool IncludeAudio { get; init; }
    public bool CopyAudio { get; init; }
    /// <summary>FFmpeg encoder name: libx264, h264_nvenc, h264_qsv, h264_amf, libx265, libsvtav1.</summary>
    public required string VideoEncoder { get; init; }
    public bool TwoPass => VideoEncoder is "libx264" or "libx265";
    public bool ToneMapHdr { get; init; }
    public bool Deinterlace { get; init; }
    /// <summary>Index into the height ladder (or -1 for "source size").</summary>
    public int Rung { get; init; }
    public double AimFraction { get; init; }
    /// <summary>True when even the smallest rung is below the bits-per-pixel floor.</summary>
    public bool TooLong { get; init; }
    public double BitsPerPixel { get; init; }
    /// <summary>True when the bitrate was capped at the source bitrate (very short clips).</summary>
    public bool CappedAtSource { get; init; }

    // Retry state, carried in the plan so the verifier stays stateless.
    public int Attempt { get; init; } = 1;
    public int RetriesOnRung { get; init; }
    public bool Raised { get; init; }
}

public sealed record ImagePlan : EncodePlan
{
    public required ImageFormat Format { get; init; }
    /// <summary>Try a lossless re-save before any lossy search.</summary>
    public bool TryLossless { get; init; }
    /// <summary>Lossy fallback when the lossless format (PNG) doesn't fit.</summary>
    public ImageFormat LossyFormat { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

public sealed record AnimatedPlan : EncodePlan
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double Fps { get; init; }
    public int Colours { get; init; } = 256;
}

public enum VerifyStatus { Pass, Retry, Fail }

public sealed record EncodeResult(
    EncodePlan Plan,
    string OutputPath,
    long OutputBytes,
    int Attempts,
    IReadOnlyList<string> Warnings);

/// <param name="Fits">True when the measured output is at or under the target. A Retry with Fits = true
/// is a bitrate raise to reclaim unused space; the current output is kept as a fallback.</param>
public sealed record VerifyResult(VerifyStatus Status, string Message, bool Fits, EncodePlan? NextPlan = null)
{
    public static VerifyResult Pass(string message = "") => new(VerifyStatus.Pass, message, true);
}

/// <summary>Which encoders work on this machine. Detected once per launch on first use.</summary>
public sealed record Capabilities
{
    public static Capabilities SoftwareOnly { get; } = new();

    public IReadOnlySet<string> Encoders { get; init; } = new HashSet<string> { "libx264", "aac" };
    /// <summary>Working hardware H.264 encoder, if any passed a real test encode.</summary>
    public string? HardwareH264 { get; init; }
    public string? HardwareName => HardwareH264 switch
    {
        "h264_nvenc" => "NVIDIA NVENC",
        "h264_qsv" => "Intel Quick Sync",
        "h264_amf" => "AMD AMF",
        _ => null,
    };
    public bool Has(string encoder) => Encoders.Contains(encoder);
}
