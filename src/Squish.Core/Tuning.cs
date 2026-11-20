namespace Squish.Core;

/// <summary>
/// Every "Starting value" from the plan lives here so the bench can sweep them.
/// Do not scatter magic numbers through the planners.
/// </summary>
public sealed record Tuning
{
    public static Tuning Default { get; } = new();

    // ---- Size budget (plan section 3)
    /// <summary>Fraction of the limit to aim for in Best mode.</summary>
    public double AimFraction { get; init; } = 0.97;
    /// <summary>Fraction of the limit to aim for with hardware encoders.</summary>
    public double FastAimFraction { get; init; } = 0.94;
    /// <summary>Anything above this fraction of the limit counts as a miss.</summary>
    public double MissFraction { get; init; } = 0.995;
    /// <summary>
    /// Video output below this fraction triggers one bitrate raise. The plan starts at 0.90, but the acceptance
    /// window starts at 0.93, and hardware encodes landed at 0.92, so the raise threshold matches the window.
    /// </summary>
    public double UnderuseFraction { get; init; } = 0.93;

    // ---- Video (plan section 6)
    public double ContainerOverhead { get; init; } = 0.005;
    public int[] HeightLadder { get; init; } = [2160, 1440, 1080, 720, 540, 480, 360];
    public double BppFloorH264 { get; init; } = 0.045;
    public double BppFloorHevcAv1 { get; init; } = 0.030;
    public double MaxRateFactor { get; init; } = 1.5;
    public double BufSizeFactor { get; init; } = 2.0;
    public double MaxFrameRate { get; init; } = 60;
    public double ShortClipSeconds { get; init; } = 3;
    /// <summary>After a miss, multiply bitrate by target / actual × this.</summary>
    public double RetryShrink { get; init; } = 0.98;
    public int RetriesPerRung { get; init; } = 2;
    public int MaxVideoAttempts { get; init; } = 8;
    /// <summary>Upper bound on a single underuse bitrate raise.</summary>
    public double MaxRaiseFactor { get; init; } = 1.35;
    public string X264Preset { get; init; } = "slow";

    /// <summary>Audio tiers: (minimum total kbps, audio kbps, channels). First match wins.</summary>
    public (int MinTotalKbps, int AudioKbps, int Channels)[] AudioTiers { get; init; } =
        [(2000, 128, 2), (1000, 96, 2), (400, 64, 2), (0, 48, 1)];

    // ---- Still images (plan section 7)
    public int ImageQualityMax { get; init; } = 95;
    public int ImageQualityMin { get; init; } = 60;
    public int ImageQualityLastResort { get; init; } = 40;
    public int ImageMaxSearchEncodes { get; init; } = 7;
    public double ImageDownscaleStep { get; init; } = 0.85;
    public int ImageLongEdgeFloor { get; init; } = 1280;
    public int JpegChromaFullAbove { get; init; } = 90;

    // ---- Animated images (plan section 7)
    public double GifMaxFps { get; init; } = 30;
    public double GifScaleStep { get; init; } = 0.85;
    public int GifLongEdgeFloor { get; init; } = 320;
    public double[] GifFpsSteps { get; init; } = [20, 15];
    public int[] GifColourSteps { get; init; } = [256, 128, 64];
    public int GifMaxAttempts { get; init; } = 14;
}
